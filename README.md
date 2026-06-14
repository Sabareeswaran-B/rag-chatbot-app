# RAG Chatbot

A production-grade Retrieval-Augmented Generation (RAG) chatbot built on **ASP.NET Core 10** and **Angular 21**. Documents are chunked, embedded, and stored in MongoDB; at query time, multi-query fan-out, cosine similarity retrieval, LLM-based reranking, and parallel moderation combine to deliver grounded, citation-backed answers.

 Link: https://ragbot-web.azurewebsites.net
---

## Table of Contents

- [Architecture](#architecture)
- [Tech Stack](#tech-stack)
- [Key Features](#key-features)
- [Data Flows](#data-flows)
  - [Upload & Indexing](#upload--indexing)
  - [Query & Answer](#query--answer)
- [API Reference](#api-reference)
- [Services](#services)
- [Data Models](#data-models)
- [Authentication & Authorization](#authentication--authorization)
- [Configuration](#configuration)
- [Getting Started](#getting-started)
- [Frontend](#frontend)

---

## Architecture

```
┌─────────────────────────────────────────────────────────────┐
│                        Angular 21 SPA                        │
│   Chat │ Upload │ Analytics │ History │ Auth                  │
└──────────────────────┬──────────────────────────────────────┘
                       │ HTTP + Bearer JWT
┌──────────────────────▼──────────────────────────────────────┐
│                   ASP.NET Core 8 API                         │
│                                                              │
│  ┌──────────┐  ┌───────────┐  ┌───────────┐  ┌───────────┐ │
│  │  Chat    │  │   File    │  │ Analytics │  │   Auth    │ │
│  │Controller│  │Controller │  │Controller │  │Controller │ │
│  └────┬─────┘  └─────┬─────┘  └─────┬─────┘  └─────┬─────┘ │
│       │               │              │               │        │
│  ┌────▼──────────────────────────────▼───────────────▼─────┐ │
│  │                    Service Layer                         │ │
│  │  ChatService  │  FileProcessingService  │  AuthService  │ │
│  │  EmbeddingService  │  RerankingService                  │ │
│  │  ModerationService │  MultiQueryService                 │ │
│  │  QueryRewriteService │  JwtService                      │ │
│  └────────────────────────┬─────────────────────────────── ┘ │
└───────────────────────────┼──────────────────────────────────┘
                            │
            ┌───────────────┼──────────────┐
            │               │              │
   ┌────────▼────────┐  ┌───▼────┐  ┌─────▼──────┐
   │    MongoDB       │  │OpenAI  │  │  OpenAI    │
   │  document_chunks │  │Embed.  │  │  Chat /    │
   │  file_metadata   │  │API     │  │  Moderation│
   │  users           │  └────────┘  └────────────┘
   │  chat_sessions   │
   │  stored_tokens   │
   │  moderation_     │
   │  violations      │
   └──────────────────┘
```

---

## Tech Stack

| Layer | Technology |
|---|---|
| Backend Framework | ASP.NET Core 10 (.NET 10) |
| Frontend Framework | Angular 21.2 (standalone, zoneless) |
| Database | MongoDB (Atlas or self-hosted) |
| Vector Search | MongoDB Atlas Vector Search / in-memory cosine similarity |
| Embeddings | OpenAI `text-embedding-3-small` (1536 dimensions) |
| Chat Completion | OpenAI `gpt-4o-mini` |
| Moderation | OpenAI `omni-moderation-latest` + `gpt-4o-mini` LLM check |
| PDF Parsing | PdfPig |
| DOCX Parsing | DocumentFormat.OpenXml |
| Auth | JWT Bearer (HS256) + BCrypt password hashing |
| Token Storage | SHA-256 hashed tokens in MongoDB with TTL index |

---

## Key Features

- **Multi-query RAG**: Generates 3 alternative phrasings of every query, fans out vector search across all variants, deduplicates by chunk ID, and rerankings with an LLM before sending context to GPT-4o-mini.
- **Parallel moderation**: Runs OpenAI's built-in moderation API and a GPT-4o-mini prompt-injection check concurrently; the first to flag a query short-circuits the pipeline.
- **Smart chunking**: 1500-character fixed chunks with sentence-boundary snapping (scans the last 100 chars for `.?!\n` before cutting), minimum 50-char threshold, 2000-chunk document cap, zero overlap.
- **Content deduplication**: SHA-256 hash of the entire uploaded file; if the hash exists in `file_metadata`, the upload returns immediately without re-chunking or re-embedding.
- **Token accounting**: Every API call's token usage (moderation, rewrite, multi-query, rerank, completion, session-naming) is summed and attributed to the user. Admins can set per-user token limits and view cost breakdowns.
- **Session management**: Auto-generated 2–5 word session names, per-user 30-session FIFO cap, full message history with token totals stored in MongoDB.
- **Refresh + remember-me auth**: Short-lived JWTs (15 min) + rotating refresh tokens (1 h), optional 30-day remember-me token stored in an httpOnly cookie. All token values hashed (SHA-256) before database storage.
- **Anonymous support**: Clients without accounts generate a stable browser fingerprint ID (UA + language + timezone + screen size, base64-encoded) passed via `X-Anonymous-Id` header.
- **Admin dashboard**: Per-user token usage, estimated cost, session counts, blocked status, violation history, and risk profile with violation-category breakdown.

---

## Data Flows

### Upload & Indexing

```
POST /api/file/upload (admin)
        │
        ▼
1. Validate file size (PDF ≤ 25 MB, others ≤ 100 MB)
        │
        ▼
2. Compute SHA-256 hash of raw bytes
        │
        ▼
3. Check file_metadata.contentHash → duplicate? Return early
        │
        ▼
4. FileProcessingService.ExtractAndChunkAsync()
   ├── Buffer IFormFile → MemoryStream
   ├── Extract text
   │     ├── .pdf  → PdfPig page-by-page text concatenation
   │     ├── .docx → OpenXml paragraph InnerText traversal
   │     └── .txt/.md/.csv → StreamReader read-to-end
   └── ChunkText()
         ├── Collapse newlines → single-space-joined clean string
         ├── ChunkSize = 1500, Overlap = 0, MaxChunks = 2000
         ├── Advance end to last sentence boundary within ±100 chars
         ├── Discard chunks < 50 chars
         └── Break when end ≥ cleanText.Length (no infinite tail)
        │
        ▼
5. EmbeddingService.GetEmbeddingsAsync(chunks)
   ├── Batch size: 50 chunks per request
   ├── Up to 3 concurrent batches (SemaphoreSlim)
   └── Model: text-embedding-3-small → float[1536] per chunk
        │
        ▼
6. MongoDbService.SaveChunksAsync()
   ├── DeleteMany existing chunks for this fileName
   └── InsertMany DocumentChunk { fileName, content, embedding,
                                    chunkIndex, contentHash, ... }
        │
        ▼
7. SaveFileMetadataAsync()
   └── { fileName, contentHash, fileSize, fileType,
          chunkCount, characterCount, uploadedBy, uploadedAt }
```

### Query & Answer

```
POST /api/chat
        │
        ▼
1. Token limit check (user.tokensUsed ≥ tokenLimit → reject)
        │
        ▼
2. Parallel moderation
   ├── ModerationService.CheckBuiltInAsync()    ← omni-moderation-latest
   │     categories: hate, violence, sexual, self-harm,
   │                 harassment, policy violation
   └── ModerationService.CheckLlmAsync()        ← gpt-4o-mini
         detects: prompt injection, illegal activity
   Either flags → save ModerationViolation, return CONTENT_MODERATED
        │
        ▼
3. Load session history (last 6 messages if sessionId present)
        │
        ▼
4. QueryRewriteService.RewriteAsync()           ← gpt-4o-mini
   Rewrite query to resolve pronoun references from history
   Skip if no history
        │
        ▼
5. Concurrent:
   ├── MultiQueryService.GenerateAsync()        ← gpt-4o-mini
   │     Generate 3 alternative phrasings
   └── EmbeddingService.GetEmbeddingAsync(rewrittenQuery)
         Returns float[1536] primary query vector
        │
        ▼
6. Vector search fan-out
   ├── Load all DocumentChunk records once (GetAllChunksAsync)
   ├── Primary query   → VectorSearchInMemory → top 10
   └── Each variation  → embed → VectorSearchInMemory → top 10
   Total: up to 40 candidate chunks
        │
        ▼
7. Deduplicate by chunk._id → unique candidate set
        │
        ▼
8. RerankingService.RerankAsync(query, candidates, topK=5) ← gpt-4o-mini
   Select top 5 by relevance; fallback to first 5 on error
        │
        ▼
9. Concurrent:
   ├── LLM completion (gpt-4o-mini)
   │     System: "Answer ONLY from context, cite with [1][2], return JSON"
   │     Context: top 5 chunks labelled [Source 1: fileName] …
   │     History: last 6 session messages
   │     Returns: { answer, reasoning, sources[] }
   └── Session name generation (if new session)
         gpt-4o-mini → 2-5 word title
        │
        ▼
10. Parse LLM JSON response (strip ```json wrapper if present)
        │
        ▼
11. Persist session
    ├── New session: create + enforce 30-session FIFO cap
    └── Append { role, content, sources[], timestamp }
        │
        ▼
12. Increment user.tokensUsed (sum of all pipeline calls)
        │
        ▼
13. Return ChatResponse { answer, sources[], reasoning?,
                           sessionId, sessionName, success }
```

---

## API Reference

### Authentication — `POST /api/auth/*`

| Method | Route | Auth | Description |
|--------|-------|------|-------------|
| GET | `/api/auth/status` | None | Returns `{ setupRequired: bool }` — whether the first admin account exists |
| POST | `/api/auth/setup` | None | Create first admin account `{ username, password }` — fails if any user exists |
| POST | `/api/auth/register` | None | Register a new non-admin user (500k default token limit) |
| POST | `/api/auth/login` | None | Login `{ username, password, rememberMe }` → `{ accessToken, refreshToken, rememberMeToken? }` |
| POST | `/api/auth/refresh` | None | Exchange `{ refreshToken }` for a new access token |
| POST | `/api/auth/remember` | None | Auto-login with `{ token }` (remember-me cookie value) |
| POST | `/api/auth/logout` | Bearer | Revoke current session tokens |

### Chat — `/api/chat`

| Method | Route | Auth | Description |
|--------|-------|------|-------------|
| POST | `/api/chat` | None (anonymous allowed) | `{ query, sessionId?, anonymousId? }` → `{ answer, sources[], reasoning?, sessionId, sessionName }` |

### Chat History — `/api/chathistory`

| Method | Route | Auth | Description |
|--------|-------|------|-------------|
| GET | `/api/chathistory` | None | List 30 most recent sessions for the caller (no messages) |
| GET | `/api/chathistory/{id}` | None | Full session with all messages |
| DELETE | `/api/chathistory/{id}` | None | Delete a specific session |
| DELETE | `/api/chathistory` | None | Delete all sessions for the caller |

### Files — `/api/file`

| Method | Route | Auth | Description |
|--------|-------|------|-------------|
| POST | `/api/file/upload` | Admin | Multipart upload (PDF ≤ 25 MB, others ≤ 100 MB) |
| GET | `/api/file/list` | Bearer | List all indexed documents with metadata |
| GET | `/api/file/download/{fileName}` | Bearer | Download reconstructed extracted text as `.txt` |
| DELETE | `/api/file/{fileName}` | Admin | Delete all chunks + metadata for a document |

### Analytics — `/api/analytics`

| Method | Route | Auth | Description |
|--------|-------|------|-------------|
| GET | `/api/analytics` | Admin | Full dashboard: summary, user stats, top sessions, risk profile |
| PATCH | `/api/analytics/users/{userId}/tokens` | Admin | Add tokens to user `{ amount: long }` |
| PATCH | `/api/analytics/users/{userId}/block` | Admin | Block/unblock user `{ blocked: bool }` |

---

## Services

### `ChatService`
The main orchestrator. Coordinates the full pipeline: token guard → moderation → session load → query rewrite → multi-query + embedding → vector fan-out → dedup → rerank → LLM completion + session naming → session persist → token accounting.

### `FileProcessingService`
Text extraction (PdfPig for PDF, OpenXml for DOCX, StreamReader for text files) and `ChunkText()`:
- Collapses all whitespace to single-spaced clean text.
- Iterates with `ChunkSize = 1500`, finds the last sentence-ending character (`.?!\n`) within 100 chars of the target boundary using `String.LastIndexOfAny`.
- Breaks immediately when `end >= cleanText.Length` (prevents infinite tail-repeat loop on overlap-adjacent chunks).

### `EmbeddingService`
Wraps OpenAI `text-embedding-3-small`. Batches chunks into groups of 50 and runs up to 3 batches concurrently via `SemaphoreSlim`.

### `ModerationService`
Runs two checks in parallel:
- **Built-in**: `omni-moderation-latest` — fast flagging of hate, violence, sexual, self-harm, harassment.
- **LLM**: `gpt-4o-mini` — prompt injection and illegal activity detection via structured prompt.
Returns the first positive result. Always returns token counts even on pass.

### `MultiQueryService`
Sends the rewritten query to `gpt-4o-mini` with a JSON-format instruction to produce 3 semantically distinct alternative phrasings. Returns an empty list (not an error) on failure.

### `QueryRewriteService`
Sends the query plus the last 6 session messages to `gpt-4o-mini` with an instruction to rewrite the message as a self-contained search query by resolving all pronoun and reference chains. Skips (returns original query) when there is no session history.

### `RerankingService`
Passes all candidate chunks and the query to `gpt-4o-mini` in JSON mode; receives an ordered list of the top 5 most relevant chunk indices. Falls back to `candidates.Take(5)` on LLM error.

### `MongoDbService`
- Manages `document_chunks` and `file_metadata` collections.
- `VectorSearchInMemory`: loads all chunks once per chat request, performs cosine similarity scoring in memory — avoids N MongoDB round-trips during multi-query fan-out.
- `AtlasVectorSearchAsync`: delegates to MongoDB Atlas `$vectorSearch` aggregation stage when `UseAtlasVectorSearch = true`.

### `AuthService`
Handles all auth flows (login, register, setup, refresh, remember-me). Refresh and remember-me tokens are generated as 32 random bytes (base64), stored as SHA-256 hashes in `stored_tokens`, and rotated on every use. Access tokens are HS256 JWTs (15 min default TTL).

### `JwtService`
Generates and validates HS256 JWTs. Claims: `sub` (userId), `unique_name` (username), `role`, `jti`. Validation returns a `ClaimsPrincipal` or `null`.

---

## Data Models

### MongoDB Collections

**`document_chunks`**
```
_id          ObjectId
fileName     string
content      string
embedding    float[]   (1536 dimensions)
chunkIndex   int
contentHash  string    (SHA-256 of source file)
uploadedAt   DateTime
fileType     string
```
Index: `fileName ASC`

**`file_metadata`**
```
_id             ObjectId
fileName        string    (unique)
contentHash     string    (unique — deduplication key)
fileSize        long      (bytes)
fileType        string    (extension without dot)
chunkCount      int
characterCount  long      (sum of chunk lengths)
uploadedBy      string
uploadedAt      DateTime
```

**`users`**
```
_id           ObjectId
username      string    (unique)
passwordHash  string    (BCrypt)
role          string    ("admin" | "user")
tokenLimit    long      (0 = unlimited)
tokensUsed    long
createdAt     DateTime
isActive      bool
isBlocked     bool
```

**`stored_tokens`**
```
_id        ObjectId
tokenHash  string    (SHA-256 of raw token value)
userId     ObjectId
tokenType  string    ("refresh" | "remember")
expiresAt  DateTime  (TTL index — auto-deleted by MongoDB)
createdAt  DateTime
isRevoked  bool
```

**`chat_sessions`**
```
_id               ObjectId
userId            string
name              string
messages[]
  role            string    ("user" | "assistant")
  content         string
  sources         string[]
  timestamp       DateTime
totalInputTokens  long
totalOutputTokens long
createdAt         DateTime
updatedAt         DateTime
```
Index: `userId + updatedAt DESC`

**`moderation_violations`**
```
_id          ObjectId
userId       string
username     string
isAnonymous  bool
query        string
category     string    (e.g. "Hate", "Prompt Injection")
createdAt    DateTime
```

---

## Authentication & Authorization

### Token Lifecycle

```
Login
  └─► Access Token (JWT, 15 min)       — sent in Authorization: Bearer header
  └─► Refresh Token (random 32B, 1 h)  — stored in localStorage
  └─► Remember-Me Token (random 32B, 30 d) — stored in httpOnly cookie [optional]

Access Token expires
  └─► Client sends Refresh Token to POST /api/auth/refresh
      └─► Old token revoked, new Access + Refresh token pair issued

Remember-Me used (page load)
  └─► POST /api/auth/remember with cookie value
      └─► Full token pair issued (remember-me token also rotated)

All raw token values are SHA-256 hashed before storage.
MongoDB TTL index on stored_tokens.expiresAt auto-purges expired records.
```

### Roles

| Role | Permissions |
|------|------------|
| `admin` | Upload files, delete files, view analytics, block users, add tokens, all user actions |
| `user` | Chat, view file list, download files, view own history |
| Anonymous | Chat (no token limit enforcement), view own anonymous session history |

### Blocked User Enforcement
A custom middleware runs early in the pipeline. For every authenticated request it fetches the user record and returns `403 USER_BLOCKED` if `isBlocked = true`. The Angular interceptor catches this code and forces a logout.

---

## Configuration

All sensitive values should be stored in .NET User Secrets or environment variables, not in `appsettings.json`.

```json
{
  "OpenAI": {
    "ApiKey": "sk-..."
  },
  "MongoDB": {
    "ConnectionString": "mongodb+srv://...",
    "DatabaseName": "ragchatbot",
    "UseAtlasVectorSearch": false
  },
  "Jwt": {
    "Secret": "<32+ character random string>",
    "Issuer": "ragchatbot-api",
    "Audience": "ragchatbot-app",
    "AccessTokenExpiryMinutes": 15,
    "RefreshTokenExpiryHours": 1,
    "RememberMeExpiryDays": 30
  }
}
```

| Key | Required | Default | Notes |
|-----|----------|---------|-------|
| `OpenAI:ApiKey` | Yes | — | Used for embeddings, chat, and moderation |
| `MongoDB:ConnectionString` | Yes | — | Atlas or local `mongodb://` URL |
| `MongoDB:DatabaseName` | No | `ragchatbot` | |
| `MongoDB:UseAtlasVectorSearch` | No | `false` | Set `true` to use Atlas `$vectorSearch`; requires a `vector_index` on `embedding` field |
| `Jwt:Secret` | Yes | — | Must be ≥ 32 chars for HS256 |
| `Jwt:AccessTokenExpiryMinutes` | No | `15` | |
| `Jwt:RefreshTokenExpiryHours` | No | `1` | |
| `Jwt:RememberMeExpiryDays` | No | `30` | |

### MongoDB Atlas Vector Search Index

If `UseAtlasVectorSearch = true`, create the following index on the `document_chunks` collection:

```json
{
  "name": "vector_index",
  "type": "vectorSearch",
  "fields": [
    {
      "type": "vector",
      "path": "Embedding",
      "numDimensions": 1536,
      "similarity": "cosine"
    }
  ]
}
```

---

## Getting Started

### Prerequisites

- .NET 10 SDK
- Node.js 20+ and npm
- MongoDB (local or Atlas)
- OpenAI API key

### Backend

```bash
cd backend
dotnet user-secrets set "OpenAI:ApiKey" "sk-..."
dotnet user-secrets set "MongoDB:ConnectionString" "mongodb://localhost:27017"
dotnet user-secrets set "Jwt:Secret" "your-32-char-minimum-secret-here"
dotnet run
# API available at https://localhost:7001
```

### Frontend

```bash
cd frontend
npm install
ng serve
# SPA available at http://localhost:4200
```

### First Run

On first launch, navigate to the app and you will be prompted to create an admin account (the `POST /api/auth/status` endpoint returns `setupRequired: true` when no users exist). After setup, log in as admin to upload documents.

---

## Frontend

Built with **Angular 21** using standalone components and zoneless change detection (`provideZonelessChangeDetection()`).

### Tabs

| Tab | Access | Description |
|-----|--------|-------------|
| Chat | All | Send queries, view grounded answers with source citations, switch sessions |
| Upload | Admin | Upload documents, view indexed file list, download/delete |
| Analytics | Admin | Token usage, cost breakdown, session stats, user blocking, violation log |
| History | All | Browse and delete past chat sessions |

### Anonymous Mode

Users who skip login get a browser-fingerprint-based anonymous ID (User-Agent + language + timezone + screen dimensions, base64-encoded, trimmed to 32 chars) sent on every request via `X-Anonymous-Id`. This ID is stable per browser session and used to associate history.

### Auth Interceptor

- Adds `Authorization: Bearer {accessToken}` to all non-auth requests.
- Adds `X-Anonymous-Id` header for unauthenticated users.
- Catches `401`: exchanges the stored refresh token for a new access token and retries the original request.
- Catches `403 USER_BLOCKED`: clears all stored tokens and redirects to login.

### Token Cost Model

```
Input tokens:  $0.00000015 / token
Output tokens: $0.00000060 / token
```
All pipeline calls (moderation, rewrite, multi-query, rerank, completion, session naming) are included in the per-user token count displayed in the analytics dashboard.
