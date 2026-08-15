namespace AgentCore.Application.Tests.Configuration;

/// <summary>
/// The worked example of section 8.1, in both forms.
/// </summary>
/// <remarks>
/// The YAML is the document as the design writes it, plus the three optional keys section 8.1 does
/// not print: <c>fallbackReply</c>, <c>refusalReply</c>, and <c>evaluation</c>. All three hold their
/// default, and the shipped
/// <c>config/example.yaml</c> holds the same document. The JSON is the same document again, and it
/// was produced by a different YAML reader so that rule 17 of section 11 tests something real.
/// </remarks>
internal static class ExampleDocument
{
    /// <summary>The section 8.1 document as YAML.</summary>
    public const string Yaml =
        """
        apiVersion: agentcore/v1
        name: service-voice
        fallbackReply: "I am sorry. I could not finish that. Please say it again."
        refusalReply: "I am sorry. I cannot help with that request."

        state:
          callerAskedForHuman: { type: boolean, default: false, writer: extractor }
          callerSaidGoodbye:   { type: boolean, default: false, writer: extractor }
          machineIdentified:   { type: boolean, default: false, writer: extractor }
          resolved:            { type: boolean, default: false, writer: extractor }
          orderStatus:         { type: string,  writer: tool, from: lookup_order.status }
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
          - { id: search_chunks, kind: builtin, uses: knowledge.search }
          - { id: read_doc,      kind: builtin, uses: knowledge.read }
          - { id: list_docs,     kind: builtin, uses: knowledge.list }
          - { id: grep_docs,     kind: builtin, uses: knowledge.grep }
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

        agents:
          defaults:
            model: { ref: reply, temperature: 0.3 }
            instructions: |
              <the stable cached prefix: persona, safety, transfer rules, and tool etiquette>
          items:
            - { id: greeter,    instructions: "<stage delta>", tools: [] }
            - { id: identifier, instructions: "<stage delta>", tools: [ lookup_order ] }
            - { id: resolver,   instructions: "<stage delta>", tools: [ search_chunks, read_doc, list_docs, grep_docs ] }
            - { id: escalator,  instructions: "<stage delta>", tools: [ create_case ] }
            - { id: closer,     instructions: "<stage delta>", tools: [] }

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

        providers:
          llm:
            - { kind: openai, model: gpt-4.1-mini, as: reply }      # the voice path, chosen on latency
            - { kind: openai, model: gpt-5.4-nano, as: fill }       # the extractor, chosen on null discipline
            - { kind: openai, model: gpt-4.1,      as: judge }      # evaluation only, chosen on judgement
          call:      { kind: telnyx-relay }        # the pipe: who carries the call and owns /v1/call
          speech:    { kind: telnyx-relay }        # the ears and mouth. Bundled here, so it matches call
          telephony: { kind: telnyx }              # dial, transfer, hang up. Not the pipe — that is call
          moderation: { kind: openai }             # reads what the CALLER said, before the model runs
          knowledge: { search: filesystem, documents: filesystem, root: ./kb }

        evaluation:
          sampleRate: 0
          judge: { ref: judge, temperature: 0 }
        """;

    /// <summary>The same document as JSON.</summary>
    public const string Json =
        """
        {
          "apiVersion": "agentcore/v1",
          "name": "service-voice",
          "fallbackReply": "I am sorry. I could not finish that. Please say it again.",
          "refusalReply": "I am sorry. I cannot help with that request.",
          "state": {
            "callerAskedForHuman": {
              "type": "boolean",
              "default": false,
              "writer": "extractor"
            },
            "callerSaidGoodbye": {
              "type": "boolean",
              "default": false,
              "writer": "extractor"
            },
            "machineIdentified": {
              "type": "boolean",
              "default": false,
              "writer": "extractor"
            },
            "resolved": {
              "type": "boolean",
              "default": false,
              "writer": "extractor"
            },
            "orderStatus": {
              "type": "string",
              "writer": "tool",
              "from": "lookup_order.status"
            },
            "failedResolveTurns": {
              "type": "integer",
              "default": 0,
              "writer": "counter",
              "increment": {
                "and": [
                  {
                    "===": [
                      {
                        "var": "stage"
                      },
                      "resolve"
                    ]
                  },
                  {
                    "!": {
                      "var": "resolved"
                    }
                  }
                ]
              }
            }
          },
          "extractor": {
            "model": {
              "ref": "fill"
            },
            "when": "after_reply"
          },
          "guards": {
            "saidGoodbye": {
              "var": "callerSaidGoodbye"
            },
            "wantsHuman": {
              "and": [
                {
                  "!": {
                    "var": "callerSaidGoodbye"
                  }
                },
                {
                  "var": "callerAskedForHuman"
                }
              ]
            },
            "identified": {
              "and": [
                {
                  "!": {
                    "var": "callerSaidGoodbye"
                  }
                },
                {
                  "!": {
                    "var": "callerAskedForHuman"
                  }
                },
                {
                  "var": "machineIdentified"
                }
              ]
            },
            "goodbyeOrFixed": {
              "or": [
                {
                  "var": "callerSaidGoodbye"
                },
                {
                  "and": [
                    {
                      "!": {
                        "var": "callerAskedForHuman"
                      }
                    },
                    {
                      "var": "resolved"
                    }
                  ]
                }
              ]
            },
            "humanOrExhausted": {
              "and": [
                {
                  "!": {
                    "var": "callerSaidGoodbye"
                  }
                },
                {
                  "or": [
                    {
                      "var": "callerAskedForHuman"
                    },
                    {
                      "and": [
                        {
                          "!": {
                            "var": "resolved"
                          }
                        },
                        {
                          ">=": [
                            {
                              "var": "failedResolveTurns"
                            },
                            3
                          ]
                        }
                      ]
                    }
                  ]
                }
              ]
            }
          },
          "tools": [
            {
              "id": "search_chunks",
              "kind": "builtin",
              "uses": "knowledge.search"
            },
            {
              "id": "read_doc",
              "kind": "builtin",
              "uses": "knowledge.read"
            },
            {
              "id": "list_docs",
              "kind": "builtin",
              "uses": "knowledge.list"
            },
            {
              "id": "grep_docs",
              "kind": "builtin",
              "uses": "knowledge.grep"
            },
            {
              "id": "lookup_order",
              "kind": "http",
              "description": "Read one order by its identifier.",
              "parameters": {
                "type": "object",
                "properties": {
                  "orderId": {
                    "type": "string"
                  }
                },
                "required": [
                  "orderId"
                ]
              },
              "request": {
                "method": "GET",
                "url": "https://api.example.com/orders/{orderId}",
                "headers": {
                  "Authorization": "Bearer ${secret:orders-api-key}"
                }
              }
            },
            {
              "id": "create_case",
              "kind": "binding",
              "binds": "CreateCase",
              "description": "Open a service case for a human agent.",
              "parameters": {
                "type": "object",
                "properties": {
                  "summary": {
                    "type": "string"
                  }
                },
                "required": [
                  "summary"
                ]
              }
            }
          ],
          "agents": {
            "defaults": {
              "model": {
                "ref": "reply",
                "temperature": 0.3
              },
              "instructions": "<the stable cached prefix: persona, safety, transfer rules, and tool etiquette>\n"
            },
            "items": [
              {
                "id": "greeter",
                "instructions": "<stage delta>",
                "tools": []
              },
              {
                "id": "identifier",
                "instructions": "<stage delta>",
                "tools": [
                  "lookup_order"
                ]
              },
              {
                "id": "resolver",
                "instructions": "<stage delta>",
                "tools": [
                  "search_chunks",
                  "read_doc",
                  "list_docs",
                  "grep_docs"
                ]
              },
              {
                "id": "escalator",
                "instructions": "<stage delta>",
                "tools": [
                  "create_case"
                ]
              },
              {
                "id": "closer",
                "instructions": "<stage delta>",
                "tools": []
              }
            ]
          },
          "policy": {
            "initial": "greeting",
            "stages": [
              {
                "id": "greeting",
                "agent": "greeter",
                "to": [
                  {
                    "stage": "identify"
                  }
                ]
              },
              {
                "id": "identify",
                "agent": "identifier",
                "to": [
                  {
                    "stage": "close",
                    "when": "saidGoodbye"
                  },
                  {
                    "stage": "escalate",
                    "when": "wantsHuman"
                  },
                  {
                    "stage": "resolve",
                    "when": "identified"
                  }
                ]
              },
              {
                "id": "resolve",
                "agent": "resolver",
                "to": [
                  {
                    "stage": "close",
                    "when": "goodbyeOrFixed"
                  },
                  {
                    "stage": "escalate",
                    "when": "humanOrExhausted"
                  }
                ]
              },
              {
                "id": "escalate",
                "agent": "escalator",
                "to": [
                  {
                    "stage": "close"
                  }
                ]
              },
              {
                "id": "close",
                "agent": "closer",
                "terminal": true
              }
            ]
          },
          "providers": {
            "llm": [
              {
                "kind": "openai",
                "model": "gpt-4.1-mini",
                "as": "reply"
              },
              {
                "kind": "openai",
                "model": "gpt-5.4-nano",
                "as": "fill"
              },
              {
                "kind": "openai",
                "model": "gpt-4.1",
                "as": "judge"
              }
            ],
            "call": {
              "kind": "telnyx-relay"
            },
            "speech": {
              "kind": "telnyx-relay"
            },
            "telephony": {
              "kind": "telnyx"
            },
            "moderation": {
              "kind": "openai"
            },
            "knowledge": {
              "search": "filesystem",
              "documents": "filesystem",
              "root": "./kb"
            }
          },
          "evaluation": {
            "sampleRate": 0,
            "judge": {
              "ref": "judge",
              "temperature": 0
            }
          }
        }
        """;
}
