"""Analyze OBS frames from the --capture-timeline synthetic fixture.

Requires numpy, Pillow, and an explicit FFmpeg executable. The default geometry
matches the observed 2560x1440 display scaled to a 1920x1080 OBS canvas.
Only synthetic fixture frames are saved; no audio is copied.
"""
import argparse
import hashlib
import json
import subprocess
from pathlib import Path

import numpy as np
from PIL import Image

parser = argparse.ArgumentParser()
parser.add_argument("video")
parser.add_argument("--ffmpeg", required=True)
parser.add_argument("--output-dir", required=True)
args = parser.parse_args()
out = Path(args.output_dir)
out.mkdir(parents=True, exist_ok=True)
width, height = 600, 500
command = [args.ffmpeg, "-v", "error", "-i", args.video, "-an", "-vf",
           f"crop={width}:{height}:0:0", "-f", "rawvideo", "-pix_fmt", "rgb24", "-"]
process = subprocess.Popen(command, stdout=subprocess.PIPE)
counts = {"protected": 0, "positive": 0, "outside_fixture": 0}
positive_hits, protected_hits, classified = [], [], []
saved = set()
index = 0
while True:
    data = process.stdout.read(width * height * 3)
    if not data:
        break
    if len(data) != width * height * 3:
        raise RuntimeError("Incomplete decoded frame")
    frame = np.frombuffer(data, dtype=np.uint8).reshape(height, width, 3)
    backdrop = frame[30:55, 30:55].astype(float).mean(axis=(0, 1))
    reference = frame[85:125, 85:125].astype(float).mean(axis=(0, 1))
    phase = "outside_fixture"
    if min(reference) > 235 and backdrop[0] < 35 and backdrop[1] > 220:
        if backdrop[2] < 35:
            phase = "protected"
        elif backdrop[2] > 220:
            phase = "positive"
    counts[phase] += 1
    if phase != "outside_fixture":
        region = frame[210:330, 210:390]
        magenta = int(((region[:, :, 0] > 80) & (region[:, :, 1] < 180) &
                       (region[:, :, 2] > 80)).sum())
        classified.append({"frame": index, "phase": phase, "magentaPixels": magenta})
        if phase == "positive" and magenta > 100:
            positive_hits.append(index)
        if phase == "protected" and magenta > 0:
            protected_hits.append({"frame": index, "magentaPixels": magenta})
        if (phase == "protected" or magenta > 100) and phase not in saved:
            Image.fromarray(frame).save(out / f"{phase}.png")
            saved.add(phase)
    index += 1
if process.wait() != 0:
    raise RuntimeError("FFmpeg decoding failed")
protected_frames = [f["frame"] for f in classified if f["phase"] == "protected"]
thumbnail_frame = protected_frames[len(protected_frames) // 2] if protected_frames else None
if thumbnail_frame is not None:
    subprocess.run([args.ffmpeg, "-v", "error", "-i", args.video, "-vf",
                    f"select=eq(n\\,{thumbnail_frame}),crop={width}:{height}:0:0",
                    "-frames:v", "1", "-update", "1", "-y", str(out / "protected.png")], check=True)
summary = {"source": str(Path(args.video).resolve()),
           "sourceSha256": hashlib.sha256(Path(args.video).read_bytes()).hexdigest(),
           "decodedFrames": index, "counts": counts,
           "positiveFramesWithMarker": len(positive_hits),
           "positiveFirstFrame": positive_hits[0] if positive_hits else None,
           "positiveLastFrame": positive_hits[-1] if positive_hits else None,
           "protectedFramesWithMarker": protected_hits,
           "protectedThumbnailFrame": thumbnail_frame,
           "positiveBeforeProtected": sum(i < protected_frames[0] for i in positive_hits) if protected_frames else 0,
           "positiveAfterProtected": sum(i > protected_frames[-1] for i in positive_hits) if protected_frames else 0,
           "geometry": "OBS 1920x1080 from 2560x1440; cropped 600x500; marker ROI x210..389,y210..329",
           "scope": "Recorded frames only, synthetic ToolTip; not a proof for other capture modes, popup types, or arbitrarily short events.",
           "frames": classified}
(out / "frame-analysis.json").write_text(json.dumps(summary, indent=2), encoding="utf-8")
print(json.dumps({k: v for k, v in summary.items() if k != "frames"}, indent=2))
