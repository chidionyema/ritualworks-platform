# Wave 3 — Deep Infrastructure, Performance & Shared Code Fix Specification

> Generated 2026-05-15. Covers BuildingBlocks, EF queries, config/deployment.
> Every item: exact file:line, root cause, fix with code, test spec.

---

## CRITICAL

### C3-01: BuildingBlocks — X-User-Id header spoofable (auth bypass across ALL services)
- **File**: `src/BuildingBlocks/CurrentUser/CurrentUserService.cs:16-19` and `src/BuildingBlocks/Extensions/HttpContextExtensions.cs:8-11`
- **Root cause**: `CurrentUserService.UserId` checks X-User-Id header FIRST, before JWT claim. Any direct caller can set this header and impersonate any user. All 13 services affected.
- **Fix**: In every service's `Program.cs` middleware pipeline, add middleware that strips `X-User-Id` from inbound requests before auth runs:
  ```csharp
  app.Use(async (ctx, next) => {
      ctx.Request.Headers.Remove("X-User-Id");
      await next();
  });
  ```
  Place BEFORE `app.UseAuthentication()`. Alternatively, change `CurrentUserService` to check JWT claim FIRST, use X-User-Id only as fallback when claim is present (trusted internal call).
- **Test spec**:
  - `Request_with_spoofed_XUserId_header_uses_jwt_claim_instead`

### C3-02: BuildingBlocks — HybridCache stampede protection broken on lock timeout
- **File**: `src/BuildingBlocks/Caching/HybridCache.cs:71-94`
- **Root cause**: When `WaitAsync(LockTimeout)` returns false (timeout), code falls through into try block and calls factory without lock. All timed-out threads hit DB simultaneously.
- **Fix**: After `WaitAsync` returns false, skip the factory and return stale L1 value or throw:
  ```csharp
  if (!lockAcquired)
  {
      logger.LogWarning("Cache stampede lock timeout for key {Key}", key);
      return default; // or return stale L1 if available
  }
  ```
- **Test spec**:
  - `GetOrCreateAsync_under_lock_contention_does_not_stampede`

### C3-03: BuildingBlocks — JWKS fetched over HTTP (MITM → full auth bypass)
- **File**: `src/BuildingBlocks/Authentication/JwksAuthenticationExtensions.cs:50`
- **Root cause**: `new HttpDocumentRetriever { RequireHttps = false }` allows JWKS over plain HTTP. MITM can serve malicious JWKS.
- **Fix**: Change to `RequireHttps = !builder.Environment.IsDevelopment()` — only allow HTTP in Development.
- **Test spec**: N/A (infrastructure config)

---

## HIGH

### H3-01: BuildingBlocks — ClockSkew = TimeSpan.Zero causes intermittent 401s
- **File**: `src/BuildingBlocks/Authentication/JwksAuthenticationExtensions.cs:82`
- **Fix**: Change to `ClockSkew = TimeSpan.FromSeconds(30)`

### H3-02: BuildingBlocks — VaultAppRoleAuthenticator creates raw HttpClient (socket exhaustion)
- **File**: `src/BuildingBlocks/Vault/VaultAppRoleAuthenticator.cs:44-46`
- **Fix**: Require `IHttpClientFactory` (remove null fallback). At bootstrap, register a named client before Vault init.

### H3-03: BuildingBlocks — DynamicCredentialsInterceptor.IsAuthError misses inner exceptions
- **File**: `src/BuildingBlocks/Persistence/DynamicCredentialsConnectionInterceptor.cs:130-132`
- **Fix**: Check `ex?.InnerException?.Message` as well. Flatten with a helper:
  ```csharp
  private static bool IsAuthError(Exception? ex) =>
      ContainsAuthKeyword(ex?.Message) || ContainsAuthKeyword(ex?.InnerException?.Message);
  ```

### H3-04: BuildingBlocks — VaultService on-demand path has no Polly retry
- **File**: `src/BuildingBlocks/Vault/VaultService.cs:225`
- **Fix**: Wrap `RefreshCredentialsInternal` call with the same Polly policy used in `RefreshCredentials`.

### H3-05: EF — AuditWriter COPY connection leak on exception
- **File**: `src/Audit/Audit.Infrastructure/Persistence/AuditWriter.cs:90-116`
- **Root cause**: If `writer.CompleteAsync()` throws, connection returned to pool in dirty COPY state.
- **Fix**: Wrap in try/finally:
  ```csharp
  await using var writer = await connection.BeginBinaryImportAsync(...);
  try { /* write rows */ await writer.CompleteAsync(); }
  catch { await writer.DisposeAsync(); throw; }
  ```

### H3-06: EF — N+1 in DisbursementService (N SellerProfile queries per payout batch)
- **File**: `src/Payouts/Payouts.Application/Disbursements/Services/DisbursementService.cs:29-36`
- **Fix**: Batch load profiles: `var profiles = await _context.SellerProfiles.Where(p => eligibleOwnerIds.Contains(p.SellerId)).ToDictionaryAsync(p => p.SellerId);`

### H3-07: EF — N+1 in MatureFundsCommand (N account queries per mature batch)
- **File**: `src/Payouts/Payouts.Application/Ledger/Commands/MatureFunds/MatureFundsCommand.cs:17-27`
- **Fix**: Same batch pattern — pre-load all SellerPayable accounts for the set of OwnerIds.

### H3-08: EF — Unbounded ToListAsync in DisbursementService and MatureFundsCommand
- **Files**: `DisbursementService.cs:29`, `MatureFundsCommand.cs:17`
- **Fix**: Add `.Take(500)` batch size. Process in pages.

### H3-09: EF — Missing index on StockReservations.OrderId (full table scan)
- **File**: `src/Catalog/Catalog.Infrastructure/CatalogDbContext.cs:163`
- **Fix**: Add `entity.HasIndex(r => r.OrderId);` and `entity.HasIndex(r => r.SagaId);` in StockReservation config. Create migration.

### H3-10: EF — Include after Skip/Take in OrderRepository (cartesian risk)
- **File**: `src/Orders/Orders.Infrastructure/Repositories/OrderRepository.cs:26-32`
- **Fix**: Move `.Include(o => o.Items)` before `.Skip()`.

---

## MEDIUM

### M3-01: BuildingBlocks — HybridCache._l1Keys grows unboundedly
- **File**: `src/BuildingBlocks/Caching/HybridCache.cs:151-168`
- **Fix**: Prune `_l1Keys` periodically or cap at max size.

### M3-02: BuildingBlocks — ResiliencePolicyFactory inconsistent wrap order
- **File**: `src/BuildingBlocks/Resilience/ResiliencePolicyFactory.cs:216-217`
- **Fix**: Change `CreateCombinedPolicy` to `Policy.WrapAsync(circuitBreaker, retryPolicy)` matching `CreatePolicy`.

### M3-03: BuildingBlocks — ClaimsPrincipalExtensions.GetUserId doesn't fall back to "sub"
- **File**: `src/BuildingBlocks/Extensions/ClaimsPrincipalExtensions.cs:8-11`
- **Fix**: `return principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? principal.FindFirstValue("sub") ?? throw ...;`

### M3-04: BuildingBlocks — TestAuthMiddleware generates new UserId per request
- **File**: `src/BuildingBlocks.Testing/Authentication/TestAuthMiddleware.cs:29`
- **Fix**: Use constant `"test-user-id"` instead of `Guid.NewGuid()`.

### M3-05: BuildingBlocks — TestAuthenticationHandler missing sub/email/scope claims
- **File**: `src/BuildingBlocks.Testing/Authentication/TestAuthenticationHandler.cs:40-49`
- **Fix**: Add `new Claim("sub", TestUserId)`, `new Claim("email", "test@test.com")`, `new Claim("scope", "openid profile")`.

### M3-06: BuildingBlocks — AuditableEntity.RowVersion not auto-configured as concurrency token
- **File**: `src/BuildingBlocks/Persistence/AuditableEntity.cs:26`
- **Fix**: Document that each DbContext MUST explicitly configure xmin. Add an extension method:
  ```csharp
  public static void AddConcurrencyToken<T>(this EntityTypeBuilder<T> builder) where T : AuditableEntity
  {
      builder.Property<uint>("xmin").HasColumnType("xid").ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();
  }
  ```

### M3-07: EF — Payouts service has zero AsNoTracking anywhere
- **Fix**: Add `.AsNoTracking()` to all read-only queries: `GetBalanceQuery`, `GetOnboardingLinkCommand`, `DisbursementService` eligible account load.

### M3-08: EF — Identity token removal uses load-then-RemoveRange
- **Files**: `IdentityRepositories.cs:146`, `RefreshTokenService.cs:52`
- **Fix**: Replace with `await _context.RefreshTokens.Where(rt => rt.UserId == userId).ExecuteDeleteAsync(ct);`

### M3-09: EF — DbContext held during S3 upload in AuditExportWorker
- **File**: `src/Audit/Audit.Infrastructure/Export/AuditExportWorker.cs:52-139`
- **Fix**: Save job status, dispose scope, perform S3 upload, re-acquire scope to mark completion.

### M3-10: BuildingBlocks — VaultService renewal loop refreshes ALL roles every iteration
- **File**: `src/BuildingBlocks/Vault/VaultService.cs:334-337`
- **Fix**: Only refresh roles where `store.IsExpiredOrNearExpiry(refreshThreshold)` is true.

### M3-11: BuildingBlocks — X-Forwarded-For spoofable in audit logs
- **File**: `src/BuildingBlocks/Audit/HttpContextExtensions.cs:36-42`
- **Fix**: Use ASP.NET Core's `ForwardedHeadersMiddleware` with trusted proxy config instead of manual header reading.

### M3-12: BuildingBlocks — ServiceDefaults hard-codes Catalog meter name for all services
- **File**: `src/BuildingBlocks/Extensions/ServiceDefaults.cs:95`
- **Fix**: Make meter/source names configurable per service, or register them dynamically based on assembly name.

---

## CONFIG, DEPLOYMENT & MIDDLEWARE FINDINGS

### C3-04: Merchant — UseAuthentication missing
- **File**: `src/Merchant/Merchant.Api/Program.cs:48-51`
- **Fix**: Add `app.UseAuthentication();` before `app.UseAuthorization();`

### H3-11: Identity — UseRateLimiter after UseAuthorization (defeats purpose)
- **File**: `src/Identity/Identity.Api/Program.cs:168-172`
- **Fix**: Move `app.UseRateLimiter();` BEFORE `app.UseAuthentication();`

### H3-12: All Dockerfiles — Running as root (all except Webhooks)
- **Fix**: Add `USER $APP_UID` in runtime stage of every Dockerfile.

### H3-13: No DB health checks anywhere
- **Fix**: In every service's DI, call `builder.Services.AddDbHealthCheck<TDbContext>()` (method exists in ServiceDefaults but is never called).

### H3-14: Identity — CORS missing (browser-facing)
- **File**: `src/Identity/Identity.Api/Program.cs`
- **Fix**: Add CORS policy allowing the portfolio site origin.

### H3-15: Location — Password in appsettings.json (ships in Docker image)
- **File**: `src/Location/Location.Api/appsettings.json:10`
- **Fix**: Remove hardcoded connection string. Use environment variable only.

### H3-16: Webhooks — DB migrations only run in Development
- **File**: `src/Webhooks/Webhooks.Api/Program.cs:62-65`
- **Fix**: Remove Development guard. Run migrations unconditionally (except Test).

### H3-17: Payments — WebhookOptions missing ValidateOnStart
- **File**: `src/Payments/Payments.Api/Program.cs:58-59`
- **Fix**: Add `.ValidateDataAnnotations().ValidateOnStart()`

### M3-13: Guest credential fallbacks in Payouts, Scheduler, Merchant, Audit
- **Fix**: Remove `?? "guest"` fallbacks. Throw on missing config.

### M3-14: Notifications — AddMassTransit missing Test guard
- **File**: `src/Notifications/Notifications.Infrastructure/DependencyInjection.cs:52`
- **Fix**: Wrap in `if (!env.IsEnvironment("Test"))`.

### M3-15: Payouts — Hangfire job registration runs in Test env
- **File**: `src/Payouts/Payouts.Api/Program.cs:43-48`
- **Fix**: Wrap in `if (!app.Environment.IsEnvironment("Test"))`.

---

## TEST CORRECTNESS FINDINGS

### H3-18: Controller unit tests are trivial mock passthroughs (Identity, Orders)
- **Files**: `tests/Identity/Identity.Unit/Controllers/*.cs`, `tests/Orders/Orders.Unit/Controllers/*.cs`
- **Fix**: Add negative cases (404, 403). Assert response body values, not just status code type.

### H3-19: DeleteProduct/UpdateProduct handlers — event publisher never verified
- **Files**: `tests/Catalog/Catalog.Unit/Commands/Products/DeleteProductCommandHandlerTests.cs:38`, `UpdateProductCommandHandlerTests.cs:43`
- **Fix**: Add `_eventPublisherMock.Verify(x => x.PublishAsync(It.Is<ProductCacheInvalidatedEvent>(...)), Times.Once)`.

### H3-20: CreateOrderCommandHandler — It.IsAny<Order>() in verify
- **File**: `tests/Orders/Orders.Unit/Commands/CreateOrderCommandHandlerTests.cs:33`
- **Fix**: Replace `It.IsAny<Order>()` with `It.Is<Order>(o => o.UserId == expectedUserId && o.TotalAmount == expectedAmount)`.

### H3-21: 16 instances of Task.Delay for test synchronization
- **Fix**: Replace with `harness.Published.Any<T>(filter, cts.Token)` or polling loop with timeout. Priority: `CdcFanOutWorkerTests`, `RefundSagaIntegrationTests`.

### M3-16: PartitionRolloverTests tests raw SQL, not the production service
- **File**: `tests/Audit/Audit.Integration/PartitionRolloverTests.cs`
- **Fix**: Rewrite to call `PartitionRolloverService.CreatePartitionForMonthAsync()` instead of inline SQL.

### M3-17: NotificationRequestConsumer template selector throws NotImplementedException in tests
- **File**: `tests/Notifications/Notifications.Unit/Consumers/NotificationRequestConsumerTests.cs`
- **Fix**: Provide a working mock template selector and test the template rendering path.

### L3-01: Search.Unit SmokeTest `True_Is_True` — delete this placeholder test
- **File**: `tests/Search/Search.Unit/SmokeTest.cs:8`
