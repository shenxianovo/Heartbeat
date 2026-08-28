# Collector Protocol Conformance Suite

`v1/collector-protocol-conformance.json` is the language-neutral executable behavior corpus for Collector
Protocol v1. A language implementation may organize its code differently, but its adapter must
interpret lifecycle ordering, acknowledgement removal, retry attempts, Gap and drain according to
these cases.

This file is not a wire-message schema or a collection of complete request/response transcripts.
HTTP JSON and stdio NDJSON bindings validate their message shapes in their implementations; this
corpus fixes only the cross-language behaviors represented by its vectors.

The .NET Collector Protocol client and the Browser Collector tests both load this file. Message
fields and strict validation live in the shared protocol/runtime code; Package and Fact payload
contracts live in their own JSON documents. Adding a cross-language behavior requires extending
this corpus and both consumers in the same change.
