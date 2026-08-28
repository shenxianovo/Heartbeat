# Collector Protocol Conformance Suite

`v1/collector-protocol-transcripts.json` is the language-neutral executable corpus for Collector
Protocol v1. A language implementation may organize its code differently, but its adapter must
interpret lifecycle ordering, acknowledgement removal, retry attempts, Gap and drain according to
these cases.

The .NET Collector Protocol client and the Browser Collector tests both load this file. The prose
specification remains authoritative for fields not represented by the current corpus; adding a
cross-language behavior requires extending the corpus and both consumers in the same change.
