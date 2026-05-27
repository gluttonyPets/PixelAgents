-- ============================================================================
-- Migration Script: Remove Frozen System Prompts from ProjectModules
-- ============================================================================
-- 
-- Purpose:
--   This script removes "systemPrompt" fields from ProjectModule.Configuration
--   to fix a bug where systemPrompt was frozen when a module was added to a
--   pipeline, preventing updates made in the AiModule from taking effect.
--
-- After this migration:
--   - ProjectModules will no longer have systemPrompt in their Configuration
--   - System prompts will be read from AiModule.Configuration (dynamic updates)
--   - Other config fields (temperature, numberOfImages, etc.) remain untouched
--
-- Usage:
--   Run this script against each tenant database that has affected ProjectModules.
--
-- IMPORTANT: 
--   This script must be run for EACH tenant database separately.
--   Replace 'tenant_xxx' with your actual tenant database name.
--
-- ============================================================================

-- Function to remove systemPrompt from a JSON configuration string
CREATE OR REPLACE FUNCTION remove_system_prompt_from_config(config_json TEXT)
RETURNS TEXT AS $$
DECLARE
    parsed_config JSONB;
    result_config JSONB;
BEGIN
    -- If config is NULL or empty, return NULL
    IF config_json IS NULL OR config_json = '' THEN
        RETURN NULL;
    END IF;
    
    -- Parse the JSON
    BEGIN
        parsed_config := config_json::JSONB;
    EXCEPTION WHEN OTHERS THEN
        -- If parsing fails, return original
        RETURN config_json;
    END;
    
    -- Remove systemPrompt key (case-insensitive)
    result_config := parsed_config - 'systemPrompt' - 'systemprompt' - 'SYSTEMPROMPT';
    
    -- If result is empty object, return NULL
    IF result_config = '{}'::JSONB THEN
        RETURN NULL;
    END IF;
    
    -- Return as text
    RETURN result_config::TEXT;
END;
$$ LANGUAGE plpgsql;

-- Update all ProjectModules to remove systemPrompt from Configuration
UPDATE "ProjectModules"
SET 
    "Configuration" = remove_system_prompt_from_config("Configuration"),
    "UpdatedAt" = NOW()
WHERE 
    "Configuration" IS NOT NULL 
    AND "Configuration" != ''
    AND (
        "Configuration"::JSONB ? 'systemPrompt' 
        OR "Configuration"::JSONB ? 'systemprompt'
        OR "Configuration"::JSONB ? 'SYSTEMPROMPT'
    );

-- Report affected rows
DO $$
DECLARE
    affected_count INTEGER;
BEGIN
    GET DIAGNOSTICS affected_count = ROW_COUNT;
    RAISE NOTICE 'Migration completed. Updated % ProjectModule records.', affected_count;
END $$;

-- Clean up the temporary function
DROP FUNCTION IF EXISTS remove_system_prompt_from_config(TEXT);

-- ============================================================================
-- End of Migration Script
-- ============================================================================
