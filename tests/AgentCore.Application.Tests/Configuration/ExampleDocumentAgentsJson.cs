namespace AgentCore.Application.Tests.Configuration
{
    /// <summary>
    /// The second half of <see cref="ExampleDocument.Json"/>: the agents, the entries, the providers and the evaluation section.
    /// </summary>
    internal static class ExampleDocumentAgentsJson
    {
        /// <summary>The JSON text. It is not a document on its own.</summary>
        internal const string Text =
            """
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
                  "create_case",
                  "search",
                  "publish"
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
          "entries": {
            "phone": {
              "fallbackReply": "Sorry — say it again.",
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
              }
            },
            "chat": {
              "agent": "webchat",
              "refusalReply": "Sorry — I can't help with that here."
            }
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
                "as": "cheap",
                "webSearch": false
              }
            ],
            "conversation": {
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
            "moderation": {
              "kind": "openai"
            },
            "embeddings": {
              "kind": "openai",
              "model": "text-embedding-3-small"
            },
            "blobs": {
              "kind": "s3",
              "endpoint": "https://s3.us-east-005.backblazeb2.com",
              "bucket": "agentcore-files",
              "region": "us-east-005"
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
                ],
                "filterable": [
                  {
                    "key": "applies_to",
                    "description": "The machine the question is about, as one tag, such as ct900 or f63-2019."
                  }
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
}
