"""Capability policy shared by the supervisor and isolated engine worker."""
import re


class KernelError(RuntimeError):
    def __init__(self, code, message):
        self.code = code
        super().__init__(message)


def require(condition, message, code="INVALID_ARGUMENT"):
    if not condition:
        raise KernelError(code, message)


def integer(value, low, high, name):
    require(type(value) is int and low <= value <= high, f"{name}: expected integer {low}..{high}")
    return value


def address(value):
    require(isinstance(value, str) and re.fullmatch(r"(?:0x)?[0-9a-fA-F]{1,16}", value),
            "address must be a hexadecimal virtual address, without expressions")
    return int(value, 16)


def text(value, name, limit=4096):
    require(isinstance(value, str) and len(value) <= limit and "\0" not in value and
            "\n" not in value and "\r" not in value, f"Invalid {name}")
    return value


def capabilities(mode, full_access):
    return {"backend": "dbgeng", "mode": mode, "query": True,
            "snapshotStable": mode == "dump", "memoryWrite": bool(full_access),
            "executionControl": bool(full_access and mode == "remote"),
            "breakpoints": bool(full_access and mode == "remote"),
            "rawCommand": bool(full_access), "userModeAttach": False,
            "localKernelLimitations": mode == "local"}


def connection_identity(connection):
    text(connection, "connection")
    require(bool(re.match(r"^(?:net|com|usb|1394):[^\r\n]+$", connection, re.I)),
            "Use net:, com:, usb: or 1394: WinDbg connection options")
    require(connection.isascii(), "Connection options must be ASCII")
    # Secret excluded from identity: a different key must not permit a second
    # engine to compete for the same transport endpoint.
    transport, options = connection.split(":", 1)
    tokens = sorted(token.strip().lower() for token in options.split(",")
                    if not token.strip().lower().startswith("key="))
    return transport.lower() + ":" + ",".join(tokens)


def query_command(query, arguments, mode):
    """No arbitrary expressions, module names or command fragments in inspect mode."""
    require(isinstance(arguments, dict), "arguments must be an object")
    simple = {"version": "vertarget", "modules": "lm", "processes": "!process 0 0",
              "threads": "!thread", "registers": "r", "stack": "kv",
              "bugcheck": ".bugcheck", "analyze": "!analyze -v", "breakpoints": "bl"}
    if query in simple:
        require(not arguments, "This query accepts no arguments")
        require(mode != "local" or query not in {"registers", "stack", "breakpoints"},
                "This operation is unavailable for local kernel debugging", "UNSUPPORTED")
        require(query not in {"bugcheck", "analyze"} or mode == "dump",
                "Crash analysis requires a dump session", "UNSUPPORTED")
        return simple[query]
    require(query == "disassemble", "Unknown query")
    require(set(arguments) <= {"address", "count"} and "address" in arguments, "Expected address and optional count")
    start = address(arguments["address"])
    count = integer(arguments.get("count", 16), 1, 128, "count")
    return f"u 0x{start:x} L0x{count:x}"


def validate_operation(mode, full_access, operation, arguments):
    require(isinstance(arguments, dict), "arguments must be an object")
    if operation == "query":
        require(set(arguments) == {"query", "arguments"}, "Invalid query arguments")
        query_command(arguments["query"], arguments["arguments"], mode)
    elif operation == "read_memory":
        require(set(arguments) == {"address", "size"}, "Invalid memory arguments")
        address(arguments["address"])
        integer(arguments["size"], 1, 65536, "size")
    elif operation == "command":
        require(full_access, "Raw commands require an explicitly opened full_access session", "CAPABILITY_DENIED")
        require(set(arguments) == {"command"}, "Invalid command arguments")
        require(bool(text(arguments["command"], "command")), "Empty command")
        # Session ownership is immutable. Target lifecycle stays in typed tools.
        require(not re.search(r"(?:^|[;\s])(?:\.(?:opendump|attach|create|detach|kill|restart|reboot|crash)|q(?:d|q)?)(?:\s|;|$)",
                              arguments["command"], re.I), "Use typed session lifecycle tools; target replacement/termination is unavailable")
    elif operation == "control":
        require(full_access and mode == "remote", "Execution control requires a remote full_access session", "CAPABILITY_DENIED")
        require(set(arguments) == {"action"} and arguments["action"] in {"continue", "step_into", "step_over"}, "Unknown control action")
    elif operation == "write_memory":
        require(full_access, "Memory writes require full_access", "CAPABILITY_DENIED")
        require(set(arguments) == {"address", "data_hex"}, "Invalid write arguments")
        address(arguments["address"])
        data = arguments["data_hex"]
        require(isinstance(data, str) and 2 <= len(data) <= 131072 and len(data) % 2 == 0 and
                re.fullmatch(r"[0-9a-fA-F]+", data), "data_hex must contain 1..65536 bytes")
    elif operation in {"set_breakpoint", "remove_breakpoint"}:
        require(full_access and mode == "remote", "Breakpoints require remote full_access", "CAPABILITY_DENIED")
        if operation == "set_breakpoint":
            require(set(arguments) == {"address"}, "Expected breakpoint address")
            address(arguments["address"])
        else:
            require(set(arguments) == {"breakpoint_id"}, "Expected breakpoint_id")
            integer(arguments["breakpoint_id"], 0, 0xfffffffe, "breakpoint_id")
    elif operation == "close":
        require(not arguments, "close accepts no arguments")
    else:
        raise KernelError("INVALID_ARGUMENT", "Unknown operation")
