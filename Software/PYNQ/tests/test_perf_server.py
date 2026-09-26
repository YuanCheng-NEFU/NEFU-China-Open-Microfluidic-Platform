"""Offline V7 PERF checks using mocked PYNQ outputs and a deterministic clock."""

import contextlib
import importlib.util
import io
import json
from pathlib import Path
import socket
import sys
import types
import unittest
from unittest.mock import patch


STUBS = {name: types.ModuleType(name) for name in (
    "pynq", "pynq.lib", "pynq.lib.pmod", "pynq.overlays", "pynq.overlays.base")}
STUBS["pynq.lib.pmod"].Pmod_IO = object
STUBS["pynq.overlays.base"].BaseOverlay = object
SOURCE = Path(__file__).resolve().parents[1] / "pynq_v6_perf_server.py"
SPEC = importlib.util.spec_from_file_location("tested_perf_server", SOURCE)
SERVER = importlib.util.module_from_spec(SPEC)
with patch.dict(sys.modules, STUBS):
    SPEC.loader.exec_module(SERVER)


class Output:
    def __init__(self):
        self.values = []

    def write(self, value):
        self.values.append(value)

    def on(self):
        self.write(1)

    def off(self):
        self.write(0)


class Clock:
    now = 100.0
    ns = 1000000

    def monotonic(self):
        return self.now

    def perf_counter_ns(self):
        self.ns += 1000
        return self.ns


class Connection:
    def __init__(self, chunks):
        self.chunks = iter(chunks)
        self.sent = []

    def settimeout(self, timeout):
        self.timeout = timeout

    def recv(self, size):
        chunk = next(self.chunks, b"")
        if isinstance(chunk, BaseException):
            raise chunk
        return chunk

    def sendall(self, content):
        self.sent.append(json.loads(content))

    def __enter__(self):
        return self

    def __exit__(self, *args):
        return False


class CsvSink:
    def __init__(self):
        self.rows = []

    def write(self, row):
        self.rows.append(row)


class PerfServerTests(unittest.TestCase):
    def setUp(self):
        self.clock = Clock()
        self.monotonic = patch.object(SERVER.time, "monotonic", self.clock.monotonic)
        self.perf = patch.object(SERVER.time, "perf_counter_ns", self.clock.perf_counter_ns)
        self.monotonic.start()
        self.perf.start()
        self.addCleanup(self.monotonic.stop)
        self.addCleanup(self.perf.stop)
        self.output = contextlib.redirect_stdout(io.StringIO())
        self.output.__enter__()
        self.addCleanup(self.output.__exit__, None, None, None)
        self.gate, self.mark, self.led = Output(), Output(), Output()
        self.controller = SERVER.GateController(self.gate, self.mark, self.led)

    def sample(self, seq=1, count=15000, **extra):
        data = {"protocol": "PYNQ_PERF_V1", "seq": seq, "count": count,
                "valid": True, "status": "OK"}
        data.update(extra)
        return self.controller.apply_sample(data)

    def arm(self):
        return self.controller.command({"command": "auto", "command_id": "arm-1"})

    def connection(self, chunks):
        connection = Connection(chunks)
        sink = CsvSink()
        SERVER.serve_connection(connection, ("127.0.0.1", 5000), self.controller, sink)
        return connection, sink

    @staticmethod
    def line(data):
        return (json.dumps(data) + "\n").encode("utf-8")

    def test_startup_low_and_disarmed(self):
        self.assertEqual(self.controller.mode, "FORCE_LOW")
        self.assertEqual(self.gate.values, [0])
        self.assertEqual(self.mark.values, [0])
        self.assertEqual(self.led.values, [0])

    def test_unarmed_sample_does_not_raise_gate(self):
        ack = self.sample()
        self.assertEqual(ack["judge"], "HIGH")
        self.assertEqual(ack["gate"], "LOW")

    def test_hysteresis_on_hold_off(self):
        self.arm()
        self.assertEqual(self.sample(1, 15000)["gate"], "HIGH")
        self.assertEqual(self.sample(2, 14950)["gate"], "HIGH")
        self.assertEqual(self.sample(3, 14900)["gate"], "LOW")
        self.assertEqual(self.sample(4, 14999)["gate"], "LOW")

    def test_invalid_samples_drive_low(self):
        for fields in ({"count": -1}, {"valid": False}, {"status": "ERROR"},
                       {"count": float("nan")}, {"count": float("inf")},
                       {"count": "invalid"}):
            with self.subTest(fields=fields):
                self.arm()
                self.sample(1)
                ack = self.sample(2, **fields)
                self.assertEqual(ack["result"], "INVALID")
                self.assertFalse(self.controller.gate_high)

    def test_threshold_validation(self):
        for threshold, hysteresis in ((-1, 0), (1, 2), (1, -1), (float("nan"), 1)):
            with self.subTest(threshold=threshold, hysteresis=hysteresis):
                with self.assertRaises(ValueError):
                    self.controller.set_parameters(threshold, hysteresis)

    def test_force_low_disarms(self):
        self.arm()
        self.sample()
        self.controller.command({"command": "force_low"})
        self.assertEqual(self.controller.mode, "FORCE_LOW")
        self.assertFalse(self.controller.gate_high)
        self.sample(2)
        self.assertFalse(self.controller.gate_high)

    def test_data_timeout_lowers_auto_gate(self):
        self.arm()
        self.sample()
        self.clock.now += 1
        self.assertTrue(self.controller.safety_tick())
        self.assertFalse(self.controller.gate_high)

    def test_client_timeout_disarms(self):
        self.arm()
        self.clock.now += 3
        self.controller.safety_tick()
        self.assertEqual(self.controller.mode, "FORCE_LOW")

    def test_heartbeat_preserves_last_operator_command(self):
        self.arm()
        self.clock.now += 2
        self.assertIsNone(self.controller.command({"command": "heartbeat"}))
        self.assertEqual(self.controller.last_command_id, "arm-1")
        self.clock.now += 2
        self.controller.safety_tick()
        self.assertEqual(self.controller.mode, "AUTO")

    def test_manual_pulse_expires(self):
        self.controller.command({"command": "pulse", "width_ms": 1000})
        self.assertTrue(self.controller.gate_high)
        self.clock.now += 0.5
        self.controller.safety_tick()
        self.assertTrue(self.controller.gate_high)
        self.clock.now += 0.5
        self.controller.safety_tick()
        self.assertFalse(self.controller.gate_high)
        self.assertEqual(self.controller.mode, "FORCE_LOW")

    def test_valid_low_count_does_not_truncate_manual_pulse(self):
        self.controller.command({"command": "pulse", "width_ms": 1000})
        self.sample(count=0)
        self.assertTrue(self.controller.gate_high)

    def test_invalid_sample_ends_manual_pulse(self):
        self.controller.command({"command": "pulse", "width_ms": 1000})
        self.sample(count=-1)
        self.assertFalse(self.controller.gate_high)

    def test_manual_pulse_bounds(self):
        for width in (0, 5001):
            with self.assertRaises(ValueError):
                self.controller.command({"command": "pulse", "width_ms": width})

    def test_new_session_resets_and_lowers(self):
        self.arm()
        self.sample()
        self.controller.reset_session("new-session")
        self.assertEqual(self.controller.mode, "FORCE_LOW")
        self.assertFalse(self.controller.gate_high)
        self.assertEqual(self.controller.counters.received, 0)
        self.assertEqual(self.controller.session_id, "new-session")

    def test_ack_protocol_fields_and_timing(self):
        ack = self.sample(5, session_id="run-1")
        self.assertEqual(ack["protocol"], "PYNQ_ACK_V1")
        self.assertEqual(ack["session_id"], "run-1")
        self.assertEqual(ack["seq"], 5)
        self.assertEqual(ack["received"], 1)
        self.assertEqual(ack["acked"], 1)
        self.assertEqual(ack["rx_to_decide_us"], 1)
        self.assertEqual(ack["rx_to_gpio_us"], 2)
        self.assertEqual(ack["rx_to_ack_us"], 3)
        self.assertEqual(self.mark.values[-2:], [1, 0])

    def test_sequence_gaps_duplicates_and_late_packets(self):
        for seq in (1, 4, 1, 2, 5):
            ack = self.sample(seq)
        self.assertEqual(ack["missing"], 2)
        self.assertEqual(ack["duplicates"], 1)
        self.assertEqual(ack["out_of_order"], 1)
        self.assertEqual(self.controller.counters.expected_seq, 6)

    def test_stats_reset(self):
        self.sample()
        self.controller.command({"command": "reset_stats"})
        self.assertEqual(self.controller.status()["received"], 0)
        self.assertEqual(self.controller.status()["latency"]["rx_to_ack_us_n"], 0)

    def test_latency_statistics(self):
        stats = SERVER.LatencyStats()
        for value in (1, 5, 3):
            stats.add(value, value, value, value)
        summary = stats.summary()
        self.assertEqual(summary["rx_to_gpio_us_median"], 3)
        self.assertEqual(summary["rx_to_gpio_us_p95"], 5)

    def test_disconnect_lowers_gate(self):
        command = self.line({"type": "command", "command": "pulse", "width_ms": 1000})
        self.connection([command, b""])
        self.assertIn(1, self.gate.values)
        self.assertEqual(self.gate.values[-1], 0)
        self.assertEqual(self.controller.mode, "FORCE_LOW")

    def test_socket_error_lowers_gate(self):
        command = self.line({"type": "command", "command": "pulse", "width_ms": 1000})
        with self.assertRaises(OSError):
            self.connection([command, OSError("disconnected")])
        self.assertFalse(self.controller.gate_high)

    def test_malformed_json_immediately_disarms(self):
        command = self.line({"type": "command", "command": "pulse", "width_ms": 1000})
        for malformed in (b"{broken\n", b"[]\n", b"null\n"):
            with self.subTest(malformed=malformed):
                connection, _ = self.connection([command, malformed, b""])
                parsed = [item for item in connection.sent if item.get("parse_errors") == 1]
                self.assertTrue(parsed)
                self.assertEqual(parsed[-1]["mode"], "FORCE_LOW")
                self.assertFalse(parsed[-1]["gate_high"])

    def test_stream_sample_ack_and_csv(self):
        arm = self.line({"type": "command", "command": "auto"})
        sample = self.line({"protocol": "PYNQ_PERF_V1", "seq": 1, "count": 16000})
        connection, sink = self.connection([arm + sample, b""])
        acks = [item for item in connection.sent if item.get("protocol") == "PYNQ_ACK_V1"]
        self.assertEqual(len(acks), 1)
        self.assertEqual(acks[0]["gate"], "HIGH")
        self.assertEqual(sink.rows, acks)

    def test_sigterm_uses_normal_shutdown_path(self):
        with self.assertRaises(KeyboardInterrupt):
            SERVER._handle_termination(SERVER.signal.SIGTERM, None)

    def test_main_interrupt_cleans_up_outputs(self):
        class Listener:
            closed = False

            def setsockopt(self, *args):
                pass

            def bind(self, *args):
                pass

            def listen(self, *args):
                pass

            def accept(self):
                self.gate.write(1)
                SERVER._handle_termination(SERVER.signal.SIGTERM, None)

            def close(self):
                self.closed = True

        listener = Listener()
        listener.gate = self.gate
        base = types.SimpleNamespace(PMODB=object(), leds=[self.led])
        with patch.object(SERVER, "BaseOverlay", return_value=base), \
                patch.object(SERVER, "Pmod_IO", side_effect=[self.gate, self.mark]), \
                patch.object(SERVER.socket, "socket", return_value=listener), \
                patch.object(SERVER.signal, "signal", return_value="previous") as handler:
            SERVER.main()
        self.assertEqual(self.gate.values[-1], 0)
        self.assertEqual(self.mark.values[-1], 0)
        self.assertTrue(listener.closed)
        self.assertEqual(handler.call_args.args, (SERVER.signal.SIGTERM, "previous"))


if __name__ == "__main__":
    unittest.main()
