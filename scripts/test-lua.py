"""Headless Lua 5.1 syntax and runtime tests. Requires lupa==2.6, never starts IX-Ray."""
from pathlib import Path
from lupa.lua51 import LuaRuntime

root = Path(__file__).resolve().parent.parent
lua = LuaRuntime(unpack_returned_tuples=True)
compile_script = lua.eval("function(text, name) local fn, err = loadstring(text, name); assert(fn, err) end")
scripts = sorted((root / "game-addon" / "scripts").glob("*.script"))
for path in scripts:
    compile_script(path.read_text(encoding="utf-8-sig"), "@" + path.name)
print(f"PASS Lua 5.1 syntax: {len(scripts)} modules", flush=True)
for fixture in sorted((root / "tests" / "lua").glob("*.lua")):
    host = LuaRuntime(unpack_returned_tuples=True)
    host.globals().SOURCE_DIR = root.as_posix()
    host.execute(fixture.read_text(encoding="utf-8"))
    print(f"PASS Lua fixture: {fixture.name}", flush=True)
