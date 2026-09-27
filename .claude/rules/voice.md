---
paths:
  - "src/AgentCore.AspNetCore/Voice/**"
  - "tests/AgentCore.AspNetCore.Tests/Voice/**"
---

# Voice

Read `.claude/rules/voice-components.md` before you change this folder. It maps the parts of one phone
call: the three loops (read, listen, write), who decides *when* the agent talks, and where the
engine (`IConversationPort`) decides *what* it says.

When your change adds, removes, or rewires a component, update the diagram in the same change.
