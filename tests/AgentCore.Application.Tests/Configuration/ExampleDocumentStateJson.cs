namespace AgentCore.Application.Tests.Configuration
{
    /// <summary>
    /// The first half of <see cref="ExampleDocument.Json"/>: the header keys, state, the extractor, the guards and the tools.
    /// </summary>
    internal static class ExampleDocumentStateJson
    {
        /// <summary>The JSON text. It is not a document on its own.</summary>
        internal const string Text =
            """
        {
          "apiVersion": "agentcore/v1",
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
            },
            {
              "id": "search",
              "kind": "builtin",
              "uses": "web.search"
            },
            {
              "id": "publish",
              "kind": "builtin",
              "uses": "file.publish"
            }
          ],
        """;
    }
}
