## Summary
<!-- Brief description of the changes -->

## Test plan
<!-- How were these changes tested? -->

### Financial & Transactional Integrity Guardrails
- [ ] Does this PR process external financial side effects (Stripe, PayPal, Payouts)? If yes, does it inherit from `ThreePhaseHandlerBase`?
- [ ] Does this PR consume asynchronous messages? If yes, does it inherit from `IdempotentConsumerBase`?
- [ ] If raw LINQ `.Take()` or `.Skip()` is used, is an explicit `.OrderBy()` appended before it?
- [ ] Are all idempotency keys mandatory (not nullable) for financial commands?
- [ ] Is there a `catch (DbUpdateConcurrencyException)` for concurrent state mutations?
