# Golden Contract Samples

These samples freeze the first-pass Photino.NET + .NET compatibility contract for novelist.

- `*.request.json` files are bridge request payload fixtures; some filenames still include `api` from the earlier route sketch.
- `*.response.json` files are bridge result payload fixtures; they do not imply HTTP transport.
- `event-*.payload.json` files are Photino bridge event payloads dispatched by the frontend compatibility adapter.
- Paths are intentionally relative to the novel workspace. Do not add machine-specific absolute paths.
- Secrets, provider API keys, and user data must never be added here.

Future bridge dispatcher and frontend adapter tests should use these files as snapshot fixtures.
