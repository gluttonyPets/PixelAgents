-- ============================================================================
-- Cleanup Script: Close stuck (zombie) ProjectExecutions
-- ============================================================================
--
-- Purpose:
--   Marks as "Cancelled" any ProjectExecution (and its StepExecutions) that is
--   still stuck in "Running"/"Waiting*" with no live cancellation token — e.g.
--   runs that hung before the cancellation fixes, or that were left over after a
--   server restart cleared the in-memory ExecutionCancellationService token.
--   Those rows show as "En curso" in the UI forever and the "Cancelar" button
--   could not clear them.
--
--   From the fixes onward the /cancel endpoint closes these cases on its own;
--   this script is only for cleaning up executions that were already stuck.
--
-- Safety:
--   A 30-minute age guard (CreatedAt) avoids cancelling a run that was just
--   launched and is legitimately working. Adjust or remove the INTERVAL to taste.
--   The script is idempotent — re-running it does no harm.
--
-- Usage:
--   Run this script against EACH tenant database separately.
--   Tip: run the SELECT preview first to review what will be affected.
--
-- ============================================================================

-- ── Preview: what would be cancelled ────────────────────────────────────────
SELECT "Id", "ProjectId", "Status", "CreatedAt"
FROM "ProjectExecutions"
WHERE ("Status" = 'Running' OR "Status" LIKE 'Waiting%')
  AND "CreatedAt" < NOW() - INTERVAL '30 minutes'
ORDER BY "CreatedAt";

-- ── Cleanup ─────────────────────────────────────────────────────────────────
BEGIN;

UPDATE "ProjectExecutions"
SET "Status" = 'Cancelled',
    "CompletedAt" = NOW(),
    "PausedAtModuleId" = NULL,
    "PausedStepData" = NULL
WHERE ("Status" = 'Running' OR "Status" LIKE 'Waiting%')
  AND "CreatedAt" < NOW() - INTERVAL '30 minutes';

DO $$
DECLARE
    exec_count INTEGER;
BEGIN
    GET DIAGNOSTICS exec_count = ROW_COUNT;
    RAISE NOTICE 'Cancelled % stuck ProjectExecution row(s).', exec_count;
END $$;

-- Close orphaned steps that are still Running/Waiting but whose parent execution
-- is no longer active (already Cancelled/Failed/Completed).
UPDATE "StepExecutions" s
SET "Status" = 'Cancelled',
    "CompletedAt" = NOW()
WHERE (s."Status" = 'Running' OR s."Status" LIKE 'Waiting%')
  AND EXISTS (
      SELECT 1 FROM "ProjectExecutions" e
      WHERE e."Id" = s."ExecutionId"
        AND e."Status" <> 'Running'
        AND e."Status" NOT LIKE 'Waiting%'
  );

DO $$
DECLARE
    step_count INTEGER;
BEGIN
    GET DIAGNOSTICS step_count = ROW_COUNT;
    RAISE NOTICE 'Cancelled % stuck StepExecution row(s).', step_count;
END $$;

COMMIT;

-- ============================================================================
-- End of Cleanup Script
-- ============================================================================
