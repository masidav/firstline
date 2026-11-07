# Product documentation (generated monthly)

Plain-English documents about the company and the product, for non-technical readers. They are rewritten
on the 1st of each month by `.github/workflows/product-docs.yml`, which runs Claude over the repository
(weekly until 2026-11-07; monthly to save on cost).

| File | What |
| --- | --- |
| `PROMPTS.md` | **Edit this to change the output.** One section per task, plus the voice rules. |
| `BUSINESS_LOGIC.md` | The product's rules in plain English: the fandom score, allocations, the draw, fairness, privacy, money. |
| `PRODUCT_DESCRIPTION.md` | The report: problem, beliefs, audience, how it works, what exists, what is next, business model. |

The job also keeps a plain-words block at the top of every folder `README.md` (between
`<!-- product-summary:start -->` and `<!-- product-summary:end -->`). Developer content below the block is
left alone.

## Running it
- **Monthly:** the 1st at 09:00 UTC, all three tasks in order (folder summaries, business logic, product
  description), then one commit to `main` with `[skip ci]`.
- **By hand:** GitHub → Actions → "Product docs" → Run workflow. Choose `all` or a single task.
- **Needs:** the repository secret `ANTHROPIC_API_KEY` (Settings → Secrets and variables → Actions). Without it
  the run fails at the Claude step and changes nothing.

## Changing the output
Edit the matching section of `PROMPTS.md` and commit. The next run uses it. To add a task, add a `## Task:`
section there and a matching choice in the workflow's `task` input.
