namespace AgentCore.Application.Tests.Configuration;

/// <summary>
/// The worked example of section 8.1, in both forms.
/// </summary>
/// <remarks>
/// The YAML is the document exactly as the design writes it. The JSON is the same document, and it
/// was produced by a different YAML reader so that rule 17 of section 11 tests something real.
/// </remarks>
internal static class ExampleDocument
{
    /// <summary>The section 8.1 document as YAML.</summary>
    public const string Yaml =
        """
        apiVersion: agentcore/v1
        name: service-voice

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
            - { id: resolver,   instructions: "<stage delta>", tools: [ search_chunks, read_doc ] }
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
          speech:    { kind: telnyx-relay }        # one vendor: STT, turn detection, TTS, interruption
          telephony: { kind: telnyx }
          knowledge: { store: zilliz, root: ./kb }
        """;

    /// <summary>The same document as JSON.</summary>
    public const string Json =
        """
        {
          "apiVersion": "agentcore/v1",
          "name": "service-voice",
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
                  "read_doc"
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
              }
            ],
            "speech": {
              "kind": "telnyx-relay"
            },
            "telephony": {
              "kind": "telnyx"
            },
            "knowledge": {
              "store": "zilliz",
              "root": "./kb"
            }
          }
        }
        """;
}
