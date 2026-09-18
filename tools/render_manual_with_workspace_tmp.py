import os
import runpy
import sys
import tempfile
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
TMP = ROOT / "outputs" / "tmp"
TMP.mkdir(parents=True, exist_ok=True)

os.environ["TMPDIR"] = str(TMP)
os.environ["TEMP"] = str(TMP)
os.environ["TMP"] = str(TMP)
tempfile.tempdir = str(TMP)

sys.argv = [
    "render_docx.py",
    str(ROOT / "outputs" / "微服务容器编排使用说明.docx"),
    "--output_dir",
    str(ROOT / "outputs" / "docx_render_microservice_manual"),
    "--emit_pdf",
]

runpy.run_path(
    r"C:\Users\Dege\.codex\plugins\cache\openai-primary-runtime\documents\26.601.10930\skills\documents\render_docx.py",
    run_name="__main__",
)
