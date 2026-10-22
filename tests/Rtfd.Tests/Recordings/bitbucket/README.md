# Bitbucket recordings

Answers shaped exactly like Bitbucket Cloud's REST API 2.0, as rtfd saw them on a real workspace on
2026-10-22, with every name, id, SHA and text replaced: the workspace is `contoso`, the repository
`web-app`, and the people `Pat Reviewer` (rtfd's posting identity) and `sam.author` (their
nicknames, which is what Bitbucket shows of an account; no emails or user names). Fields rtfd
doesn't read are mostly trimmed. Don't put a real workspace's answers here: this repository is
public.

What the shapes show:

- A PR names its commits by their first 12 characters (`source.commit.hash`).
- The comment list keeps `resolution` empty (`{}`) or leaves it out, resolved or not; the single
  comment (`comment-5002.json`) has it, and `inline.outdated`.
- A reply carries its thread's `inline` too, and may answer another reply.
- rtfd's summary ends with a link reference definition, `[//]: # (rtfd sha=... run=...)`, which
  Bitbucket renders as nothing (an HTML comment shows as text there); each of its inline comments
  ends with `[//]: # (rtfd review=<summary id>)`.
