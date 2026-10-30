# The same steps as the other two builds, by name: export, ingest, ask, eval.
# Secrets come from 1Password at run time: `op run --env-file=.env.op -- make <target>`.
CHUNKER = ../fruitless-research/site
PG      = /Applications/Postgres.app/Contents/Versions/latest/bin
DB     ?= support_rag
API    ?= http://localhost:5080

.PHONY: export db run ingest ask eval test migrate

## 1. chunk the public sources with the Cloudflare build's own chunker (same chunks, same allowlist)
export:
	cd $(CHUNKER) && npm run build >/dev/null && node scripts/support-index.mjs --dump $(CURDIR)/data/chunks.jsonl
	cp $(CHUNKER)/support/eval.jsonl data/eval.jsonl

## 2. a local database with pgvector (Postgres.app bundles the extension)
db:
	$(PG)/createdb $(DB) 2>/dev/null || true
	$(PG)/psql -d $(DB) -c 'CREATE EXTENSION IF NOT EXISTS vector;'

## 3. schema: EF Core migrations, applied by the API on boot; this applies them by hand
migrate:
	dotnet ef database update --project src/SupportRag.Core --startup-project src/SupportRag.Api

## 4. the API (ingests on first boot when the table is empty)
run:
	dotnet run --project src/SupportRag.Api

## re-ingest now (needs RAG_ADMIN_KEY in the environment of both sides)
ingest:
	curl -sS -X POST $(API)/admin/ingest -H "X-Admin-Key: $$RAG_ADMIN_KEY" | python3 -m json.tool

## ask one question:  make ask Q="Where do I put the .frlicense file on a Mac?"
ask:
	curl -sS -X POST $(API)/ask -H 'content-type: application/json' -d "$$(python3 -c 'import json,sys; print(json.dumps({"question": sys.argv[1]}))' "$(Q)")" | python3 -m json.tool

## 5. the 30-case eval set, in-process against the same pipeline; exit 1 below 80 % strict
eval:
	dotnet run --project src/SupportRag.Eval -- --json eval/latest.json --min 0.8

test:
	dotnet test
