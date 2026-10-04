namespace AgentCore.Application.Tests.Configuration
{
    /// <summary>
    /// The worked example document, in both forms.
    /// </summary>
    internal static class ExampleDocument
    {
        /// <summary>
        /// The last line of the <c>providers:</c> block, for a test that splices its own provider block
        /// in after it.
        /// </summary>
        public const string LastProviderLine = "    citation: source-locator";

        /// <summary>
        /// The one-line <c>providers.blobs</c> entry, for a test that takes it out or swaps its own in.
        /// </summary>
        public const string BlobsLine =
            "  blobs: { kind: s3, endpoint: https://s3.us-east-005.backblazeb2.com, bucket: agentcore-files, region: us-east-005 }";

        /// <summary>The worked example document as YAML.</summary>
        public const string Yaml =
            """
        apiVersion: agentcore/v1
        fallbackReply: "I am sorry. I could not finish that. Please say it again."
        refusalReply: "I am sorry. I cannot help with that request."

        state:
          callerAskedForHuman: { type: boolean, default: false, writer: extractor }
          callerSaidGoodbye:   { type: boolean, default: false, writer: extractor }
          machineIdentified:   { type: boolean, default: false, writer: extractor }
          resolved:            { type: boolean, default: false, writer: extractor }
          orderStatus:         { type: string,  writer: tool, from: lookup_order.status }
          brand:      { type: string, writer: extractor, description: "The brand of the caller's machine.", enum: [sole, spirit] }
          applies_to: { type: string, writer: extractor, description: "The model, as printed on the machine.", enum: [f63, f65, f80] }
          failedResolveTurns:
            type: integer
            default: 0
            writer: counter
            increment:
              and:
                - { "===": [ { var: stage }, "resolve" ] }
                - { "!": { var: resolved } }

        extractor:
          model: { ref: fill }
          when: after_reply

        guards:
          saidGoodbye:
            { var: callerSaidGoodbye }
          wantsHuman:
            and:
              - { "!": { var: callerSaidGoodbye } }
              - { var: callerAskedForHuman }
          identified:
            and:
              - { "!": { var: callerSaidGoodbye } }
              - { "!": { var: callerAskedForHuman } }
              - { var: machineIdentified }
          goodbyeOrFixed:
            or:
              - { var: callerSaidGoodbye }
              - and:
                  - { "!": { var: callerAskedForHuman } }
                  - { var: resolved }
          humanOrExhausted:
            and:
              - { "!": { var: callerSaidGoodbye } }
              - or:
                  - { var: callerAskedForHuman }
                  - and:
                      - { "!": { var: resolved } }
                      - { ">=": [ { var: failedResolveTurns }, 3 ] }

        tools:
          - id: lookup_order
            kind: http
            description: Read one order by its identifier.
            parameters:
              type: object
              properties: { orderId: { type: string } }
              required: [ orderId ]
            request:
              method: GET
              url: "https://api.example.com/orders/{orderId}"
              headers: { Authorization: "Bearer ${secret:orders-api-key}" }
          - id: create_case
            kind: binding
            binds: CreateCase
            description: Open a service case for a human agent.
            parameters:
              type: object
              properties: { summary: { type: string } }
              required: [ summary ]
          - { id: search, kind: builtin, uses: web.search }
          - { id: publish, kind: builtin, uses: file.publish }
        agents:
          defaults:
            model: { ref: reply, temperature: 0.3 }
            instructions: |
              <the stable cached prefix: persona, safety, transfer rules, and tool etiquette>
            knowledge: { mode: prefetch, limit: 5, citations: false }
          items:
            - { id: greeter,    instructions: "<stage delta>", tools: [] }
            - { id: identifier, instructions: "<stage delta>", tools: [ lookup_order ] }
            - { id: resolver,   instructions: "<stage delta>", tools: [] }
            - { id: escalator,  instructions: "<stage delta>", tools: [ create_case ] }
            - { id: closer,     instructions: "<stage delta>", tools: [] }
            - { id: analyst, instructions: "<stage delta>", tools: [ lookup_order, create_case, search, publish ],
                knowledge: { mode: tool, limit: 8, citations: true, scoped: false } }
            - { id: webchat, instructions: "<stage delta>", tools: [ lookup_order ],
                knowledge: { mode: tool, citations: false } }

        entries:
          phone:
            fallbackReply: "Sorry — say it again."
            policy:
              initial: greeting
              stages:
                - id: greeting
                  agent: greeter
                  to: [ { stage: identify } ]
                - id: identify
                  agent: identifier
                  to:
                    - { stage: close,    when: saidGoodbye }
                    - { stage: escalate, when: wantsHuman }
                    - { stage: resolve,  when: identified }
                - id: resolve
                  agent: resolver
                  to:
                    - { stage: close,    when: goodbyeOrFixed }
                    - { stage: escalate, when: humanOrExhausted }
                - id: escalate
                  agent: escalator
                  to: [ { stage: close } ]
                - id: close
                  agent: closer
                  terminal: true
          chat:
            agent: webchat
            refusalReply: "Sorry — I can't help with that here."

        providers:
          llm:
            - { kind: openai, model: gpt-4.1-mini, as: reply }      # the voice path, chosen on latency
            - { kind: openai, model: gpt-5.4-nano, as: fill }       # the extractor, chosen on null discipline
            - { kind: openai, model: gpt-4.1,      as: judge }      # evaluation only, chosen on judgement
            - { kind: openai, model: gpt-4.1-nano, as: cheap, webSearch: false }
          conversation:      { kind: telnyx-relay }        # the pipe: who carries the conversation and owns /v1/{entry}/call
          speech:                                  # the ears and the mouth, named one role at a time
            stt: { kind: telnyx-relay }            # recognition. Bundled here, so it matches conversation
            tts: { kind: telnyx-relay }            # synthesis. Bundled here, so it matches conversation
          moderation: { kind: openai }             # reads what the CALLER said, before the model runs
          embeddings: { kind: openai, model: text-embedding-3-small }
          blobs: { kind: s3, endpoint: https://s3.us-east-005.backblazeb2.com, bucket: agentcore-files, region: us-east-005 }
          knowledge:
            kind: qdrant
            endpoint: https://qdrant.example.com:6334
            collection: kb
            vector: dense
            fields:
              id: card_id
              body: body
              lexical: text
              source: source.ref
              locator: source.locator
              authority: authority
            scope:
              template: "facets.{key}"
              wildcard:
                value: "*"
                facets: [brand, applies_to]
              fromState: [brand, applies_to]
              filterable:
                - key: applies_to
                  description: "The machine the question is about, as one tag, such as ct900 or f63-2019."
            links:
              field: see_also
              lookup: uuid5
              prefix: "kb:"
            analyzer: none
            citation: source-locator

        evaluation:
          sampleRate: 0
          judge: { ref: judge, temperature: 0 }
        """;

        /// <summary>The same document as JSON, kept in two halves so that no file passes the size limit.</summary>
        public const string Json = ExampleDocumentStateJson.Text + "\n" + ExampleDocumentAgentsJson.Text;
    }
}
