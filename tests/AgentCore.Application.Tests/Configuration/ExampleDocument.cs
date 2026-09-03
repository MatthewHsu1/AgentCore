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
    /// <summary>
    /// The last line of the <c>providers:</c> block, for a test that splices its own provider block
    /// in after it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three test files used to keep a private copy of the one-line providers.knowledge entry and
    /// anchor on that. When the block grew to ten lines, every copy stopped matching, Replace
    /// silently did nothing, and eighteen tests failed on a provider that had never been written
    /// into the document at all. One anchor, owned by the document, cannot drift out of step with it.
    /// </para>
    /// <para>
    /// It must stay the LAST line of providers:. A splice after any earlier line lands inside the
    /// block that follows, which is a different failure again -- valid YAML, wrong nesting, and a
    /// provider silently belonging to the wrong parent.
    /// </para>
    /// </remarks>
    public const string LastProviderLine = "    citation: source-locator";

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
          - { id: draw,          kind: builtin, uses: ui.draw, model: { ref: cheap } }
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
            knowledge: { mode: prefetch, limit: 5, citations: false }
          items:
            - { id: greeter,    instructions: "<stage delta>", tools: [] }
            - { id: identifier, instructions: "<stage delta>", tools: [ lookup_order ] }
            - { id: resolver,   instructions: "<stage delta>", tools: [] }
            - { id: escalator,  instructions: "<stage delta>", tools: [ create_case ] }
            - { id: closer,     instructions: "<stage delta>", tools: [] }
            - { id: analyst, instructions: "<stage delta>", tools: [ lookup_order, create_case ],
                knowledge: { mode: tool, limit: 8, citations: true, scoped: false } }
            - { id: webchat, instructions: "<stage delta>", tools: [ lookup_order ],
                knowledge: { mode: tool, citations: false } }

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
            - { kind: openai, model: gpt-4.1-nano, as: cheap }      # ui.draw only, chosen on price
          call:      { kind: telnyx-relay }        # the pipe: who carries the call and owns /v1/call
          speech:                                  # the ears and the mouth, named one role at a time
            stt: { kind: telnyx-relay }            # recognition. Bundled here, so it matches call
            tts: { kind: telnyx-relay }            # synthesis. Bundled here, so it matches call
          telephony: { kind: telnyx }              # dial, transfer, hang up. Not the pipe — that is call
          moderation: { kind: openai }             # reads what the CALLER said, before the model runs
          embeddings: { kind: openai, model: text-embedding-3-small }
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
            "brand": {
              "type": "string",
              "writer": "extractor",
              "description": "The brand of the caller's machine.",
              "enum": [
                "sole",
                "spirit"
              ]
            },
            "applies_to": {
              "type": "string",
              "writer": "extractor",
              "description": "The model, as printed on the machine.",
              "enum": [
                "f63",
                "f65",
                "f80"
              ]
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
              "id": "draw",
              "kind": "builtin",
              "uses": "ui.draw",
              "model": {
                "ref": "cheap"
              }
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
              "instructions": "<the stable cached prefix: persona, safety, transfer rules, and tool etiquette>\n",
              "knowledge": {
                "mode": "prefetch",
                "limit": 5,
                "citations": false
              }
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
                "tools": []
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
              },
              {
                "id": "analyst",
                "instructions": "<stage delta>",
                "tools": [
                  "lookup_order",
                  "create_case"
                ],
                "knowledge": {
                  "mode": "tool",
                  "limit": 8,
                  "citations": true,
                  "scoped": false
                }
              },
              {
                "id": "webchat",
                "instructions": "<stage delta>",
                "tools": [
                  "lookup_order"
                ],
                "knowledge": {
                  "mode": "tool",
                  "citations": false
                }
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
              },
              {
                "kind": "openai",
                "model": "gpt-4.1-nano",
                "as": "cheap"
              }
            ],
            "call": {
              "kind": "telnyx-relay"
            },
            "speech": {
              "stt": {
                "kind": "telnyx-relay"
              },
              "tts": {
                "kind": "telnyx-relay"
              }
            },
            "telephony": {
              "kind": "telnyx"
            },
            "moderation": {
              "kind": "openai"
            },
            "embeddings": {
              "kind": "openai",
              "model": "text-embedding-3-small"
            },
            "knowledge": {
              "kind": "qdrant",
              "endpoint": "https://qdrant.example.com:6334",
              "collection": "kb",
              "vector": "dense",
              "fields": {
                "id": "card_id",
                "body": "body",
                "lexical": "text",
                "source": "source.ref",
                "locator": "source.locator",
                "authority": "authority"
              },
              "scope": {
                "template": "facets.{key}",
                "wildcard": {
                  "value": "*",
                  "facets": [
                    "brand",
                    "applies_to"
                  ]
                },
                "fromState": [
                  "brand",
                  "applies_to"
                ]
              },
              "links": {
                "field": "see_also",
                "lookup": "uuid5",
                "prefix": "kb:"
              },
              "analyzer": "none",
              "citation": "source-locator"
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
