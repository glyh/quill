#!/usr/bin/env python3
"""Ablate one refinement channel in RefineContext: pass the channel through
untouched (skipping that channel), leaving every other channel rewritten.

The point is to prove a conformance case *pins* a channel: neutralise the channel,
watch the case flip, then confirm every other channel leaves it passing. A channel
whose ablation changes nothing is a channel no case is testing.

Usage:  python3 ablate.py <channel>
        python3 ablate.py --list
"""
import sys

SRC = "src/Quill.Compiler/Elaborator.Patterns.cs"

CHANNELS = {
    "names": (
        "        Names = ctx.Names.ToImmutableDictionary(n => n.Key, n => Refined(ctx, level, replacement, n.Value)),",
        "        Names = ctx.Names,  // ABLATED",
    ),
    "self-entry": (
        "        SelfEntry = ctx.SelfEntry is { } self ? Refined(ctx, level, replacement, self) : null,",
        "        SelfEntry = ctx.SelfEntry,  // ABLATED",
    ),
    "resume-entry": (
        "        ResumeEntry = ctx.ResumeEntry is { } resume ? Refined(ctx, level, replacement, resume) : null,",
        "        ResumeEntry = ctx.ResumeEntry,  // ABLATED",
    ),
    "opened": (
        "        Opened = ctx.Opened.ToImmutableDictionary(o => o.Key, o => o.Value.ToImmutableDictionary(m => m.Key, m => Refined(ctx, level, replacement, m.Value))),",
        "        Opened = ctx.Opened,  // ABLATED",
    ),
    "self-methods": (
        "        SelfMethods = ctx.SelfMethods.ToImmutableDictionary(m => m.Key, m => Substitute(ctx, level, replacement, m.Value)),",
        "        SelfMethods = ctx.SelfMethods,  // ABLATED",
    ),
    "constructor-entries": (
        """        ConstructorEntries = ctx.ConstructorEntries.ToImmutableDictionary(c => c.Key, c => c.Key < level
            ? c.Value
            : (Substitute(ctx, level, replacement, c.Value.Type), Substitute(ctx, level, replacement, c.Value.TypeType), c.Value.Constructor)),""",
        "        ConstructorEntries = ctx.ConstructorEntries,  // ABLATED",
    ),
    "evidence": (
        """        Evidence = ctx.Evidence.Select(e => e with
        {
            Args = [.. e.Args.Select(a => Substitute(ctx, level, replacement, a))],
            Type = Substitute(ctx, level, replacement, e.Type),
        }).ToImmutableList(),""",
        "        Evidence = ctx.Evidence,  // ABLATED",
    ),
}

if len(sys.argv) != 2 or sys.argv[1] in ("-h", "--help"):
    print(__doc__)
    sys.exit(2)

if sys.argv[1] == "--list":
    print("\n".join(CHANNELS))
    sys.exit(0)

name = sys.argv[1]
if name not in CHANNELS:
    print(f"unknown channel {name!r}; try --list", file=sys.stderr)
    sys.exit(2)

old, new = CHANNELS[name]
text = open(SRC).read()
# Not unique means the channel was refactored or renamed: refuse loudly rather than
# patching the wrong site (or none), which would silently report a pinned channel.
count = text.count(old)
if count != 1:
    print(f"{name}: pattern not unique ({count} matches in {SRC})", file=sys.stderr)
    sys.exit(1)
open(SRC, "w").write(text.replace(old, new))
print(f"{name}: ablated")
