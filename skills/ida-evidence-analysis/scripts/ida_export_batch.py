"""IDA-side bounded export. Load with runpy; call export_batch(out,start,count).

Does not patch bytes or types. Hex-Rays can automatically refine the IDB.
"""
import json
from pathlib import Path


def export_batch(output_dir, start=0, count=50):
    import ida_funcs
    import ida_hexrays
    import ida_lines
    import ida_nalt
    import idautils

    if start < 0 or not 1 <= count <= 500:
        raise ValueError("start >= 0 and 1 <= count <= 500 required")
    root = Path(output_dir)
    root.mkdir(parents=True, exist_ok=True)
    functions = list(idautils.Functions())
    selected = functions[start:start + count]
    rows = []
    if not ida_hexrays.init_hexrays_plugin():
        raise RuntimeError("Hex-Rays unavailable")
    for ea in selected:
        f = ida_funcs.get_func(ea)
        row = {"ea": hex(ea), "name": ida_funcs.get_func_name(ea),
               "end": hex(f.end_ea), "chunks": [(hex(a), hex(b)) for a, b in idautils.Chunks(ea)]}
        failure = ida_hexrays.hexrays_failure_t()
        try:
            cfunc = ida_hexrays.decompile(ea, failure)
            if not cfunc:
                raise RuntimeError(failure.desc())
            lines = [ida_lines.tag_remove(line.line) for line in cfunc.get_pseudocode()]
            row.update(status="ok", lines=len(lines))
            row["warnings"] = [{"ea": hex(w.ea), "message": str(w.text)} for w in cfunc.get_warnings()]
            (root / (hex(ea) + ".c")).write_text("\n".join(lines) + "\n", encoding="utf-8")
        except Exception as exc:
            row.update(status="failed", error=str(exc))
        rows.append(row)
    data = {"input": ida_nalt.get_input_file_path(), "start": start,
            "next": start + len(selected), "snapshot_total": len(functions),
            "after_total": len(list(idautils.Functions())), "rows": rows}
    path = root / ("batch-%06d.json" % start)
    path.write_text(json.dumps(data, ensure_ascii=False, indent=2), encoding="utf-8")
    return {"manifest": str(path), "next": data["next"],
            "snapshot_total": data["snapshot_total"], "after_total": data["after_total"],
            "ok": sum(r["status"] == "ok" for r in rows), "failed": sum(r["status"] != "ok" for r in rows)}
