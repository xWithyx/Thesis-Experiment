# Aggregate Report

Generated: 2026-01-29 11:58:11 UTC

## Files Generated

- `runs_flat.csv` — one row per RunRecord
- `summary_by_variant.csv` — metrics per variant (A, B, C)
- `summary_by_status.csv` — counts per FinalStatus per variant
- `summary_by_project.csv` — metrics per project per variant

## Overview

- Total JSON files parsed: **142** (failed: 0)
- Total runs: **142**
  - Variant A: 50 runs
  - Variant B: 54 runs
  - Variant C: 38 runs

## Pass Rates by Variant

| Variant | Total | Completed | Build Pass % | Test Pass % | Stable Pass % | Flaky |
|---------|-------|-----------|-------------|-------------|---------------|-------|
| A | 50 | 0 | 100% | 60% | 0% | 0 |
| B | 54 | 7 | 16,7% | 13% | 13% | 0 |
| C | 38 | 6 | 15,8% | 15,8% | 15,8% | 0 |

## Coverage & Mutation (completed runs only)

| Variant | N | Avg Line Cov % | Avg Branch Cov % | Avg Mutation Score % |
|---------|---|---------------|-----------------|---------------------|
| B | 7 | 0,0% | 0,0% | 0,0% |
| C | 6 | 19,7% | 0,0% | 0,0% |

## Token Usage

| Variant | Total Tokens | Avg Tokens/Run | Total Prompt | Total Completion |
|---------|-------------|---------------|-------------|-----------------|
| A | 0 | 0 | 0 | 0 |
| B | 83.961 | 1.555 | 37.230 | 46.731 |
| C | 223.340 | 5.877 | 104.236 | 119.104 |

## Status Breakdown

| Variant | Status | Count |
|---------|--------|-------|
| A | baseline_complete | 30 |
| A | test_failed | 20 |
| B | build_failed | 34 |
| B | completed | 7 |
| B | no_test_project | 11 |
| B | test_failed | 2 |
| C | build_failed | 32 |
| C | completed | 6 |

## Notes

- Coverage and mutation metrics are only computed for `completed` runs (stable pass).
- Variant A = existing tests (baseline), B = single-shot LLM, C = repair-loop LLM.
- Flaky runs passed Gate 1+2 but had inconsistent results across 3 stability runs.
