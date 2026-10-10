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
        if data.get("id") != request_id:
            raise IdaError("IDA_TRANSPORT_ERROR", "IDA response identity mismatch")
        if "error" in data:
            raise IdaError("IDA_RPC_ERROR", str(data["error"]))
        return data["result"]
    finally:
        connection.close()


def tool_value(result):
    if result.get("isError"):
        raise IdaError("IDA_TOOL_ERROR", "IDA tool failed", result=result)
    if "structuredContent" in result:
        return result["structuredContent"]
    content = result.get("content", [])
    if len(content) == 1 and content[0].get("type") == "text":
        try:
            return json.loads(content[0]["text"])
        except ValueError:
            pass
    return result
