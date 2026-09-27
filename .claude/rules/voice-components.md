---
paths:
  - "src/AgentCore.AspNetCore/Voice/**"
  - "tests/AgentCore.AspNetCore.Tests/Voice/**"
---

# Voice components

```mermaid
flowchart TB
  classDef vendor fill:#fde7d9,stroke:#c2410c,color:#1d1d1f
  classDef port fill:#e0ecff,stroke:#1d4ed8,color:#1d1d1f
  classDef turn fill:#e6f6ea,stroke:#15803d,color:#1d1d1f
  classDef speech fill:#f3e8ff,stroke:#7e22ce,color:#1d1d1f
  classDef engine fill:#fff4c2,stroke:#a16207,color:#1d1d1f

  Caller(["📞 Caller via Telnyx"])

  subgraph Route["Routing"]
    MapCall["MapCall<br/>/v1/{entry}/call"]
    Boot["AgentCoreBoot<br/>ConversationHandler"]
  end

  subgraph Vendor["Vendors/TelnyxRelay (one per call)"]
    Conn["TelnyxRelayConnection"]
    Pump["JsonWebSocketPump<br/>read loop"]
    Reader["TelnyxRelayFrameReader"]
    In["TelnyxRelayInput<br/>(IConversationInputPort)"]
    Out["TelnyxRelayOutput<br/>(IConversationOutputPort)"]
    Sender["JsonWebSocketSender<br/>write loop"]
    Obs["ConnectionTaskObserver"]
  end

  subgraph Turn["Turn taking"]
    Loop["VoiceConversationLoop"]
    UTH["UserTurnHandler"]
    Act["VoiceActivity"]
  end

  subgraph Speech["Speech control"]
    VS["VoiceSession<br/>agent/user state, away timer"]
    Sched["SpeechScheduler<br/>queue + background speeches"]
    Handle["SpeechHandle<br/>one per speech"]
    Pipe["PipelineReply<br/>step loop"]
    Say["SayReply<br/>fixed text"]
    Fwd["TextForwarding"]
    Hear["ReplyHearing<br/>what caller heard"]
    Fill["ToolFillerScope<br/>→ FillerScheduler"]
    Met["TurnMetrics"]
  end

  subgraph Engine["AgentCore.Application"]
    Stream["EngineReplyStream"]
    Sessions["IConversationSessions"]
    CS["ConversationSession<br/>(IConversationPort)"]
  end

  Caller <-->|WebSocket JSON| MapCall
  MapCall --> Boot --> Conn
  Conn --> Pump & Sender & Loop
  Pump --> Reader --> In
  In -->|"ConversationInput:<br/>Started / Utterance / Barge"| Loop
  Loop -->|GetOrOpen / Close| Sessions --> CS
  Loop -->|interim / final| UTH
  Loop -->|Barge| Act
  Loop -->|Barge: StopAsync| Out
  UTH -->|TryGenerateReply| Act
  UTH --> VS
  Act --> Stream
  Act --> Pipe
  Act -->|schedule| Sched
  VS --> Sched
  VS -->|Say| Say
  Sched -->|authorize| Handle
  Pipe --> Handle
  Pipe -->|read step text| Stream
  Stream -->|StartTurnAsync / Cut| CS
  Pipe --> Fwd -->|BeginReply / SpeakAsync / CompleteAsync| Out
  Say --> Fwd
  Pipe --> Hear -->|TurnCut| Stream
  Pipe --> Fill -->|Say| VS
  Pipe --> Met
  Out --> Sender -->|JSON frames| Caller
  Conn --> Obs

  class Conn,Pump,Reader,In,Out,Sender,Obs vendor
  class MapCall,Boot port
  class Loop,UTH,Act turn
  class VS,Sched,Handle,Pipe,Say,Fwd,Hear,Fill,Met speech
  class Stream,Sessions,CS engine
```

```mermaid
sequenceDiagram
  autonumber
  participant T as Telnyx
  participant P as Pump + Input
  participant L as VoiceConversationLoop
  participant U as UserTurnHandler
  participant A as VoiceActivity
  participant S as SpeechScheduler
  participant R as PipelineReply
  participant E as EngineReplyStream → ConversationSession
  participant O as Output + Sender

  T->>P: setup frame
  P->>L: Started(conversationId)
  L->>L: GetOrOpen session, make VoiceActivity + UserTurnHandler
  T->>P: interim transcript
  P->>L: Utterance(IsFinal=false)
  L->>U: OnInterimTranscript → user Speaking
  T->>P: final transcript
  P->>L: Utterance(IsFinal=true)
  L->>U: OnFinalTranscript
  U->>A: InterruptByFinalTranscript (cut old speech)
  U->>A: TryGenerateReply(text)
  A->>E: Start engine turn
  A->>S: ScheduleSpeech(handle)
  S->>R: authorize speech
  R->>R: wait for user silence
  loop each model step
    E-->>R: step text
    R->>O: BeginReply / SpeakAsync / CompleteAsync
    O->>T: speak frames
    opt step calls a tool
      R->>R: WaitForToolsAsync (filler may Say)
      R->>S: re-schedule (force) for next step
    end
  end
  R->>S: speech done → agent Listening, away timer armed
  Note over T,O: Barge-in: Telnyx sends Barge → Loop calls Output.StopAsync<br/>and VoiceActivity.InterruptByAudioActivity → ReplyHearing cuts the engine turn to what was heard
```
