# -*- coding: utf-8 -*-
"""
NEFU-China iDEC PYNQ-Z2 integrity and response timing server (V7.0 PERF).

Protocol:
    Windows -> PYNQ: PYNQ_PERF_V1, one JSON object and seq per line.
    PYNQ -> Windows: PYNQ_ACK_V1, one JSON acknowledgment per sample.

Output pins:
    GATE     = PMOD B Pin 1 (JB1_P, index 0)
    RX_MARK  = PMOD B Pin 2 (JB1_N, index 1), sample-processing scope marker.
    GND      = PMOD B Pin 5 or Pin 11.

Operating states:
    Startup LOW; AUTO valid-data timeout 1 s -> GATE LOW.
    Client timeout 3 s -> FORCE_LOW; every new connection starts LOW.
"""

from __future__ import print_function

import csv
import json
import math
import os
import signal
import socket
import time

from pynq.lib.pmod import Pmod_IO
from pynq.overlays.base import BaseOverlay


HOST = "0.0.0.0"
PORT = 5000

GATE_PMOD_INDEX = 0
MARK_PMOD_INDEX = 1
LED_INDEX = 0

DATA_TIMEOUT_S = 1.0
CLIENT_TIMEOUT_S = 3.0
STATUS_INTERVAL_S = 0.5
CSV_FLUSH_INTERVAL_S = 0.5
LATENCY_HISTORY_CAP = 20000

INBOUND_PROTOCOL = "PYNQ_PERF_V1"
ACK_PROTOCOL = "PYNQ_ACK_V1"


def _pct(sorted_values, p):
    if not sorted_values:
        return 0.0
    if len(sorted_values) == 1:
        return float(sorted_values[0])
    rank = max(0, min(len(sorted_values) - 1, int(math.ceil(p * len(sorted_values)) - 1)))
    return float(sorted_values[rank])


class IntegrityCounters(object):
    """Integrity counters, reset for each new connection."""

    def __init__(self):
        self.reset()

    def reset(self):
        self.received = 0          # Successfully parsed samples.
        self.acked = 0             # Sample ACKs generated.
        self.missing = 0           # Missing sequence slots observed.
        self.duplicates = 0        # Repeated sequence numbers.
        self.out_of_order = 0      # Previously unseen late sequence numbers.
        self.invalid_slots = 0     # Invalid CH297 slots (valid=0 / count<0).
        self.parse_errors = 0      # Lines that failed JSON decoding.
        self.protocol_errors = 0   # Invalid protocol fields.
        self.expected_seq = None   # Next expected sequence number.
        self.seen = set()          # Sequence numbers seen in this session.
        self.last_seq = None
        self.last_count = None
        self.last_valid = None
        self.last_judge = None
        self.last_gate = None


class LatencyStats(object):
    """PYNQ processing durations from perf_counter_ns deltas, in us."""

    def __init__(self):
        self.rx_to_decide = []
        self.decide_to_gpio = []
        self.rx_to_gpio = []
        self.rx_to_ack = []

    def add(self, rx_to_decide_us, decide_to_gpio_us, rx_to_gpio_us, rx_to_ack_us):
        for bucket, value in (
            (self.rx_to_decide, rx_to_decide_us),
            (self.decide_to_gpio, decide_to_gpio_us),
            (self.rx_to_gpio, rx_to_gpio_us),
            (self.rx_to_ack, rx_to_ack_us),
        ):
            bucket.append(value)
            if len(bucket) > LATENCY_HISTORY_CAP:
                del bucket[0]

    def summary(self):
        out = {}
        for name, bucket in (
            ("rx_to_decide_us", self.rx_to_decide),
            ("decide_to_gpio_us", self.decide_to_gpio),
            ("rx_to_gpio_us", self.rx_to_gpio),
            ("rx_to_ack_us", self.rx_to_ack),
        ):
            s = sorted(bucket)
            out[name + "_median"] = _pct(s, 0.50)
            out[name + "_p95"] = _pct(s, 0.95)
            out[name + "_p99"] = _pct(s, 0.99)
            out[name + "_max"] = s[-1] if s else 0.0
            out[name + "_n"] = len(s)
        return out


class GateController(object):
    def __init__(self, gate_output, mark_output, led):
        self.gate = gate_output
        self.mark = mark_output
        self.led = led
        self.mode = "FORCE_LOW"
        self.gate_high = False
        self.threshold = 15000.0
        self.hysteresis = 100.0
        self.count = -1.0
        self.sample_index = 0
        self.last_valid_data = 0.0
        self.last_contact = time.monotonic()
        self.pulse_until = 0.0
        self.message = "Startup: PMOD LOW"
        self.last_command = ""
        self.last_command_id = ""
        self.last_command_result = "Waiting for a control command"
        self.last_command_time = ""
        self.last_pulse_width_ms = 0
        self.gate_transitions = 0
        self.connects = 0
        self.counters = IntegrityCounters()
        self.latency = LatencyStats()
        self.session_id = ""
        self.set_gate(False)
        self.set_mark(False)

    def reset_session(self, session_id):
        self.connects += 1
        self.session_id = session_id or ("session_" + time.strftime("%Y%m%d_%H%M%S"))
        self.counters = IntegrityCounters()
        self.latency = LatencyStats()
        self.mode = "FORCE_LOW"
        self.set_gate(False)
        self.set_mark(False)
        self.message = "New connection; integrity counters reset"

    def set_gate(self, state):
        requested = bool(state)
        changed = requested != self.gate_high
        self.gate_high = requested
        if self.gate_high:
            self.gate.write(1)
            self.led.on()
        else:
            self.gate.write(0)
            self.led.off()
        if changed:
            self.gate_transitions += 1
            print(
                "[V7] GATE -> %s (transition #%d)" %
                ("HIGH" if self.gate_high else "LOW", self.gate_transitions),
                flush=True)

    def set_mark(self, state):
        try:
            self.mark.write(1 if state else 0)
        except Exception:
            pass

    def set_parameters(self, threshold, hysteresis):
        threshold = float(threshold)
        hysteresis = float(hysteresis)
        if (not math.isfinite(threshold) or
                not math.isfinite(hysteresis) or
                threshold < 0 or hysteresis < 0 or hysteresis > threshold):
            raise ValueError("invalid threshold/hysteresis")
        self.threshold = threshold
        self.hysteresis = hysteresis
        self.message = "Threshold parameters updated"

    def _on_ack_timestamps(self, rx_ns, decide_ns, gpio_ns, ack_ns):
        rx_to_decide_us = (decide_ns - rx_ns) / 1000.0
        decide_to_gpio_us = (gpio_ns - decide_ns) / 1000.0
        rx_to_gpio_us = (gpio_ns - rx_ns) / 1000.0
        rx_to_ack_us = (ack_ns - rx_ns) / 1000.0
        self.latency.add(
            rx_to_decide_us,
            decide_to_gpio_us,
            rx_to_gpio_us,
            rx_to_ack_us)
        return (
            rx_to_decide_us,
            decide_to_gpio_us,
            rx_to_gpio_us,
            rx_to_ack_us)

    def apply_sample(self, data):
        rx_ns = time.perf_counter_ns()
        self.last_contact = time.monotonic()
        c = self.counters

        seq = data.get("seq")
        if seq is None:
            seq = data.get("sample_index")
        try:
            seq = int(seq)
        except (TypeError, ValueError):
            c.protocol_errors += 1
            raise ValueError("missing/invalid seq")

        session_id = str(data.get("session_id", "") or "")
        if session_id and session_id != self.session_id:
            self.session_id = session_id

        try:
            count = float(data.get("count", -1.0))
        except (TypeError, ValueError):
            count = -1.0
        valid_flag = bool(data.get("valid", count >= 0.0))
        status = str(data.get("status", "OK") or "OK")
        self.set_parameters(
            data.get("threshold", self.threshold),
            data.get("hysteresis", self.hysteresis))

        self.mark.write(1)  # RX_MARK starts the sample-processing scope interval.
        self.sample_index = seq
        self.count = count if math.isfinite(count) else -1.0
        valid = valid_flag and self.count >= 0.0 and status == "OK"

        # Sequence accounting: gaps, duplicates and late arrivals.
        if c.expected_seq is None:
            c.expected_seq = seq
        if seq == c.expected_seq:
            pass
        elif seq > c.expected_seq:
            c.missing += int(seq - c.expected_seq)
            print(
                "[INTEGRITY] missing seq %d..%d" % (c.expected_seq, seq - 1),
                flush=True)
        else:
            if seq in c.seen:
                c.duplicates += 1
            else:
                c.out_of_order += 1
            print(
                "[INTEGRITY] late/duplicate seq=%d (expected=%d)" %
                (seq, c.expected_seq),
                flush=True)
        c.expected_seq = max(c.expected_seq, seq + 1)
        c.seen.add(seq)
        c.received += 1
        c.last_seq = seq
        c.last_count = self.count
        c.last_valid = 1 if valid else 0

        # Threshold decision with ON/OFF hysteresis.
        decide_ns = time.perf_counter_ns()
        judge = "LOW"
        if not valid:
            judge = "INVALID"
            self.set_gate(False)
            self.message = "Invalid sample; GATE LOW"
            c.invalid_slots += 1
        else:
            self.last_valid_data = time.monotonic()
            off_threshold = self.threshold - self.hysteresis
            if not self.gate_high and self.count >= self.threshold:
                judge = "HIGH"
            elif self.gate_high and self.count <= off_threshold:
                judge = "LOW"
            else:
                judge = "HIGH" if self.gate_high else "LOW"

            if self.mode == "AUTO":
                if judge == "HIGH" and not self.gate_high:
                    self.set_gate(True)
                    self.message = "Count reached threshold; GATE HIGH"
                elif judge == "LOW" and self.gate_high:
                    self.set_gate(False)
                    self.message = "Count reached OFF threshold; GATE LOW"

        gpio_ns = time.perf_counter_ns()
        c.last_judge = judge
        c.last_gate = "HIGH" if self.gate_high else "LOW"
        c.acked += 1
        ack_ns = time.perf_counter_ns()

        (
            rx_to_decide_us,
            decide_to_gpio_us,
            rx_to_gpio_us,
            rx_to_ack_us,
        ) = self._on_ack_timestamps(rx_ns, decide_ns, gpio_ns, ack_ns)

        ack = {
            "protocol": ACK_PROTOCOL,
            "session_id": self.session_id,
            "seq": seq,
            "rx_count": self.count,
            "threshold": self.threshold,
            "judge": judge,
            "gate": c.last_gate,
            "result": "SUCCESS" if valid else "INVALID",
            "rx_timestamp": rx_ns,
            "decision_timestamp": decide_ns,
            "gpio_timestamp": gpio_ns,
            "ack_timestamp": ack_ns,
            "rx_to_decide_us": round(rx_to_decide_us, 3),
            "decide_to_gpio_us": round(decide_to_gpio_us, 3),
            "rx_to_gpio_us": round(rx_to_gpio_us, 3),
            "rx_to_ack_us": round(rx_to_ack_us, 3),
            "received": c.received,
            "acked": c.acked,
            "missing": c.missing,
            "duplicates": c.duplicates,
            "out_of_order": c.out_of_order,
            "invalid_slots": c.invalid_slots,
        }
        self.mark.write(0)  # RX_MARK ends the sample-processing scope interval.
        print(
            "[ACK] seq=%d count=%s valid=%d judge=%s gate=%s "
            "rx2gpio_us=%.1f rx2ack_us=%.1f "
            "missing=%d dup=%d ooo=%d invalid=%d" %
            (seq, self.count, 1 if valid else 0, judge, c.last_gate,
             rx_to_gpio_us, rx_to_ack_us,
             c.missing, c.duplicates, c.out_of_order, c.invalid_slots),
            flush=True)
        return ack

    def command(self, data):
        self.last_contact = time.monotonic()
        command = str(data.get("command", ""))
        command_id = str(data.get("command_id", "no-id"))
        if command != "heartbeat":
            print("[V7] RX command #%s: %s" % (command_id, command), flush=True)

        if command == "hello":
            self.message = "NEFU-China iDEC Total Control connected"
            self._record_command(command, command_id, "Handshake complete")
        elif command == "heartbeat":
            return None
        elif command == "set_params":
            self.set_parameters(
                data.get("threshold", self.threshold),
                data.get("hysteresis", self.hysteresis))
            self._record_command(command, command_id, "Threshold and hysteresis applied on PYNQ")
        elif command == "auto":
            self.mode = "AUTO"
            self.set_gate(False)
            self.message = "AUTO armed; waiting for valid samples"
            self._record_command(command, command_id, "AUTO armed")
        elif command == "force_low":
            self.mode = "FORCE_LOW"
            self.set_gate(False)
            self.message = "Total Control requested FORCE_LOW"
            self._record_command(command, command_id, "Applied; GATE=LOW")
        elif command == "pulse":
            width_ms = int(data.get("width_ms", 100))
            if width_ms < 1 or width_ms > 5000:
                raise ValueError("pulse width must be 1..5000 ms")
            self.mode = "PULSE"
            self.pulse_until = time.monotonic() + width_ms / 1000.0
            self.set_gate(True)
            self.message = "Manual test pulse HIGH"
            self.last_pulse_width_ms = width_ms
            self._record_command(command, command_id, "Accepted; GATE=HIGH until pulse completion")
        elif command == "reset_stats":
            self.counters = IntegrityCounters()
            self.latency = LatencyStats()
            self.message = "Integrity and latency statistics reset"
            self._record_command(command, command_id, "Statistics reset")
        elif command == "get_stats":
            self.message = "Statistics included in the next status response"
            self._record_command(command, command_id, "Statistics requested")
        else:
            raise ValueError("unknown command: " + command)

        print(
            "[V7] TX ACK #%s: %s" %
            (self.last_command_id, self.last_command_result),
            flush=True)
        return {
            "type": "command_ack",
            "command": command,
            "command_id": command_id,
            "result": self.last_command_result,
        }

    def _record_command(self, command, command_id, result):
        self.last_command = command
        self.last_command_id = command_id
        self.last_command_result = result
        self.last_command_time = time.strftime("%Y-%m-%d %H:%M:%S")

    def safety_tick(self):
        now = time.monotonic()
        changed = False
        if self.mode == "PULSE" and now >= self.pulse_until:
            self.mode = "FORCE_LOW"
            self.set_gate(False)
            self.message = "Test pulse complete; GATE LOW"
            self.last_command_result = "Complete"
            print("[V7] PULSE complete; GATE -> LOW", flush=True)
            changed = True
        if (self.mode == "AUTO" and self.gate_high and
                now - self.last_valid_data >= DATA_TIMEOUT_S):
            self.set_gate(False)
            self.message = "CH297 data timeout; GATE LOW"
            changed = True
        if now - self.last_contact >= CLIENT_TIMEOUT_S:
            if self.gate_high or self.mode != "FORCE_LOW":
                self.mode = "FORCE_LOW"
                self.set_gate(False)
                self.message = "Total Control timeout; GATE LOW"
                changed = True
        return changed

    def status(self):
        c = self.counters
        summary = self.latency.summary()
        return {
            "type": "status",
            "protocol": "PYNQ_STATUS_V1",
            "session_id": self.session_id,
            "mode": self.mode,
            "gate_high": self.gate_high,
            "count": self.count,
            "threshold": self.threshold,
            "hysteresis": self.hysteresis,
            "sample_index": self.sample_index,
            "message": self.message,
            "pmod": "PMOD B Pin1=GATE Pin2=RX_MARK",
            "last_command": self.last_command,
            "last_command_id": self.last_command_id,
            "last_command_result": self.last_command_result,
            "last_command_time": self.last_command_time,
            "last_pulse_width_ms": self.last_pulse_width_ms,
            "gate_transitions": self.gate_transitions,
            "connects": self.connects,
            "received": c.received,
            "acked": c.acked,
            "missing": c.missing,
            "duplicates": c.duplicates,
            "out_of_order": c.out_of_order,
            "invalid_slots": c.invalid_slots,
            "parse_errors": c.parse_errors,
            "protocol_errors": c.protocol_errors,
            "last_seq": c.last_seq,
            "latency": summary,
        }


def send_json(conn, payload):
    line = (json.dumps(
        payload, ensure_ascii=False, separators=(",", ":")) + "\n").encode("utf-8")
    conn.sendall(line)


class AckCsvWriter(object):
    def __init__(self):
        self.writer = None
        self.file_handle = None
        self.last_flush = 0.0
        self.path = None

    def ensure(self, session_id):
        if self.writer is not None:
            return
        name = "pynq_ack_%s.csv" % (session_id or "unsession")
        self.path = os.path.join(os.getcwd(), name)
        self.file_handle = open(self.path, "w", newline="")
        self.writer = csv.writer(self.file_handle)
        self.writer.writerow([
            "session_id", "seq", "rx_count", "pynq_judge", "gate",
            "rx_to_decide_us", "decide_to_gpio_us", "rx_to_gpio_us",
            "rx_to_ack_us", "result",
        ])

    def write(self, ack):
        self.ensure(ack.get("session_id", ""))
        self.writer.writerow([
            ack.get("session_id", ""),
            ack.get("seq", ""),
            ack.get("rx_count", ""),
            ack.get("judge", ""),
            ack.get("gate", ""),
            ack.get("rx_to_decide_us", ""),
            ack.get("decide_to_gpio_us", ""),
            ack.get("rx_to_gpio_us", ""),
            ack.get("rx_to_ack_us", ""),
            ack.get("result", ""),
        ])
        now = time.monotonic()
        if now - self.last_flush >= CSV_FLUSH_INTERVAL_S:
            self.file_handle.flush()
            self.last_flush = now

    def close(self):
        if self.file_handle is not None:
            try:
                self.file_handle.flush()
            except Exception:
                pass
            try:
                self.file_handle.close()
            except Exception:
                pass
        self.writer = None
        self.file_handle = None


def serve_connection(conn, address, controller, ack_csv):
    print("[V7] Total control connected: %s:%s" % address, flush=True)
    conn.settimeout(0.1)
    controller.reset_session("")
    controller.set_gate(False)
    controller.set_mark(False)
    controller.message = "Total Control connected; waiting for commands"
    send_json(conn, controller.status())

    buffer = b""
    last_status = 0.0
    pending_command_acks = []

    try:
        while True:
            changed = controller.safety_tick()
            now = time.monotonic()

            try:
                chunk = conn.recv(4096)
                if not chunk:
                    print("[V7] Windows closed the TCP connection", flush=True)
                    break
                buffer += chunk
            except socket.timeout:
                chunk = None

            while b"\n" in buffer:
                line, buffer = buffer.split(b"\n", 1)
                if not line.strip():
                    continue
                try:
                    data = json.loads(line.decode("utf-8"))
                    if not isinstance(data, dict):
                        raise ValueError("message must be a JSON object")
                except Exception as exc:
                    controller.counters.parse_errors += 1
                    controller.mode = "FORCE_LOW"
                    controller.set_gate(False)
                    controller.set_mark(False)
                    controller.message = "Invalid JSON message; GATE LOW"
                    print("[V7] PARSE ERROR: %s" % exc, flush=True)
                    changed = True
                    continue

                message_type = data.get("type")
                protocol = data.get("protocol", "")
                if message_type == "sample" or protocol == INBOUND_PROTOCOL:
                    try:
                        ack = controller.apply_sample(data)
                    except Exception as exc:
                        controller.counters.protocol_errors += 1
                        controller.message = "Command/sample error: %s" % exc
                        controller.set_gate(False)
                        print("[V7] SAMPLE ERROR: %s" % exc, flush=True)
                        changed = True
                        continue
                    send_json(conn, ack)
                    ack_csv.write(ack)
                elif message_type == "command":
                    try:
                        result = controller.command(data)
                        if result is not None:
                            pending_command_acks.append(result)
                    except Exception as exc:
                        controller.message = "Command error: %s" % exc
                        controller.set_gate(False)
                        print("[V7] COMMAND ERROR: %s" % exc, flush=True)
                else:
                    controller.counters.protocol_errors += 1
                    controller.message = "Unknown message type"
                    controller.set_gate(False)
                changed = True

            for item in pending_command_acks:
                send_json(conn, item)
            pending_command_acks = []

            if changed or now - last_status >= STATUS_INTERVAL_S:
                send_json(conn, controller.status())
                last_status = now
    finally:
        controller.mode = "FORCE_LOW"
        controller.set_gate(False)
        controller.set_mark(False)
        controller.message = "Total Control disconnected; PMOD LOW"
        print("[V7] Waiting for total control reconnect", flush=True)


def _handle_termination(signum, frame):
    raise KeyboardInterrupt


def main():
    print("=" * 76, flush=True)
    print("NEFU-China iDEC PYNQ-Z2 V7.0 PERF - integrity + latency server", flush=True)
    print("GATE=PMOD B Pin1  RX_MARK=PMOD B Pin2  GND=Pin5/11", flush=True)
    print("=" * 76, flush=True)

    base = BaseOverlay("base.bit")
    gate_output = Pmod_IO(base.PMODB, GATE_PMOD_INDEX, "out")
    mark_output = Pmod_IO(base.PMODB, MARK_PMOD_INDEX, "out")
    led = base.leds[LED_INDEX]
    controller = GateController(gate_output, mark_output, led)
    ack_csv = AckCsvWriter()

    server = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    server.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    server.bind((HOST, PORT))
    server.listen(1)
    print("[V7] Listening on %s:%d" % (HOST, PORT), flush=True)

    previous_sigterm = signal.signal(signal.SIGTERM, _handle_termination)
    try:
        while True:
            conn, address = server.accept()
            try:
                with conn:
                    serve_connection(conn, address, controller, ack_csv)
            except (OSError, ValueError) as exc:
                print("[V7] Connection ended: %s" % exc, flush=True)
            finally:
                controller.mode = "FORCE_LOW"
                controller.set_gate(False)
                controller.set_mark(False)
                ack_csv.close()
                print("[V7] Waiting for total control reconnect", flush=True)
    except KeyboardInterrupt:
        print("[V7] Stop requested", flush=True)
    finally:
        controller.mode = "FORCE_LOW"
        controller.set_gate(False)
        controller.set_mark(False)
        ack_csv.close()
        server.close()
        signal.signal(signal.SIGTERM, previous_sigterm)
        print("[V7] PMOD LOW; server stopped", flush=True)


if __name__ == "__main__":
    main()
