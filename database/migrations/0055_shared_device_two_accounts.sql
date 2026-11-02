-- shared_device fires at two accounts on one phone, not three (owner decision 2026-11-02).
--
-- 0054 started the rule at min_accounts 3 so that families sharing a phone would not reach staff review. The owner
-- chose 2: it catches a couple's second account (one person on two accounts is the case the rule exists for), and
-- the extra reviews are accepted. The window (90 days) and the cooldown (30 days) are unchanged, and so are the other
-- six rules from 0054, which stay provisional. docs/security/bot-and-shared-device-detection.md.

UPDATE fraud_rules
   SET params = params || '{"min_accounts": 2}'::jsonb,
       description = 'A push device the fan used in the last days days was registered by min_accounts or more accounts in that time (2 since 2026-11-02; a second account on one phone goes to staff review)',
       updated_at = now()
 WHERE rule_key = 'shared_device';
