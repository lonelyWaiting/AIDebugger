"""Private, authenticated loopback JSON-RPC transport; no retries of commands."""
import http.client
import json
import uuid


class IdaError(RuntimeError):
    def __init__(self, code, message, **details):
        self.payload = {"code": code, "message": message, **details}
        super().__init__(json.dumps(self.payload, ensure_ascii=False))


def rpc(port, token, method, params=None, timeout=3):
    request_id = uuid.uuid4().hex
    connection = http.client.HTTPConnection("127.0.0.1", port, timeout=timeout)
    try:
        connection.request("POST", "/mcp?ext=dbg", json.dumps({
            "jsonrpc": "2.0", "id": request_id, "method": method,
            "params": params or {},
        }), {"Content-Type": "application/json", "Authorization": "Bearer " + token})
        response = connection.getresponse()
        body = response.read(8 * 1024 * 1024 + 1)
        if response.status != 200 or len(body) > 8 * 1024 * 1024:
            raise IdaError("IDA_TRANSPORT_ERROR", "Invalid or oversized IDA response", status=response.status)
        data = json.loads(body)
        if not isinstance(data, dict) or data.get("id") != request_id:
            raise IdaError("IDA_TRANSPORT_ERROR", "IDA response identity mismatch")
        if ("error" in data) == ("result" in data):
            raise IdaError("IDA_TRANSPORT_ERROR", "IDA response must contain exactly one result or error")
        if "error" in data:
            raise IdaError("IDA_RPC_ERROR", str(data["error"]))
        return data["result"]
    except (http.client.HTTPException, ValueError) as error:
        # The request may already have executed even when its response cannot
        # be read or decoded. Lifecycle callers preserve this uncertainty.
        raise IdaError("IDA_TRANSPORT_ERROR", "Incomplete or invalid IDA response") from error
    finally:
        connection.close()


def tool_value(result):
    if not isinstance(result, dict):
        raise IdaError("IDA_TRANSPORT_ERROR", "IDA tool response must be an object")
    if result.get("isError"):
        raise IdaError("IDA_TOOL_ERROR", "IDA tool failed", result=result)
    if "structuredContent" in result:
        return result["structuredContent"]
    content = result.get("content", [])
    if not isinstance(content, list) or any(not isinstance(item, dict) for item in content):
        raise IdaError("IDA_TRANSPORT_ERROR", "Invalid IDA tool content")
    if len(content) == 1 and content[0].get("type") == "text":
        if not isinstance(content[0].get("text"), str):
            raise IdaError("IDA_TRANSPORT_ERROR", "Invalid IDA tool text")
        try:
            return json.loads(content[0]["text"])
        except ValueError:
            pass
    return result
