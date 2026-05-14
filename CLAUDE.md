# Token Optimization Rules
- Be maximally concise. No preamble, no filler, no restating the request.
- For code tasks: output only the code change, no explanation unless asked.
- Use haiku subagents for all exploration/research tasks.
- Never read full files — always use offset/limit after Grep/Glob to target exact lines.
- Parallelize all independent tool calls.
- Don't list what you're "about to do" — just do it.
- Skip confirmation for safe, reversible actions (edits, reads, tests).

# Project
- .NET 9.0 microservices platform (Clean Architecture)
- See `.claude/projects/*/memory/` for full architecture reference
