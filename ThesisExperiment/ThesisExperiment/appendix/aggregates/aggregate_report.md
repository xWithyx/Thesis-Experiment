# Aggregate Report

Generated: 2026-01-28 22:10:26 UTC

## Files Generated

- `runs_flat.csv` — one row per RunRecord
- `summary_by_variant.csv` — metrics per variant (A, B, C)
- `summary_by_status.csv` — counts per FinalStatus per variant
- `summary_by_project.csv` — metrics per project per variant

## Overview

- Total JSON files parsed: **50** (failed: 0)
- Total runs: **50**
  - Variant A: 50 runs

## Pass Rates by Variant

| Variant | Total | Completed | Build Pass % | Test Pass % | Stable Pass % | Flaky |
|---------|-------|-----------|-------------|-------------|---------------|-------|
| A | 50 | 0 | 100% | 60% | 0% | 0 |

## Coverage & Mutation (completed runs only)

_No completed runs found._

## Token Usage

| Variant | Total Tokens | Avg Tokens/Run | Total Prompt | Total Completion |
|---------|-------------|---------------|-------------|-----------------|
| A | 0 | 0 | 0 | 0 |

## Status Breakdown

| Variant | Status | Count |
|---------|--------|-------|
| A | baseline_complete | 30 |
| A | test_failed | 20 |

## Notes

- Coverage and mutation metrics are only computed for `completed` runs (stable pass).
- Variant A = existing tests (baseline), B = single-shot LLM, C = repair-loop LLM.
- Flaky runs passed Gate 1+2 but had inconsistent results across 3 stability runs.
