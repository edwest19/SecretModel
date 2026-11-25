// Written by Claude Opus 5.5 (Anthropic) for SecretModel, 2026-11-25. AI-generated.
//
// Host skeleton. It deliberately maps no endpoints yet: every endpoint except /health must require
// a validated client certificate (README SM-AUTH-001), and that arrives with the API task. Until
// then, every request gets 404.

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.Run();
