# Expense Tracker

Personal expense tracker. Pulls transactions from PKO Bank Polski via Enable Banking API.

## Stack
- backend/: .NET 10 Web API, EF Core + SQLite
- frontend/: React + TypeScript (Vite)

## Rules
- Never read, print or commit files in secrets/ or *.pem - only reference the path
- Config (application id, key path) via dotnet user-secrets, never hardcoded
- Keep changes minimal; don't refactor unrelated code
- Shared logic goes into helpers, no duplication

## Enable Banking
- Base URL: https://api.enablebanking.com
- Auth: JWT RS256, header kid = application id, claims iss="enablebanking.com", aud="api.enablebanking.com", iat, exp (iat+3600)
- Flow: GET /aspsps?country=XX -> POST /auth -> user redirect -> callback ?code= -> POST /sessions -> GET /accounts/{uid}/transactions
- Docs: https://enablebanking.com/docs/api/reference/
