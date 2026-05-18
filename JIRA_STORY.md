# Jira Story: Port `spNGQMS_GetClientRebuttalAccountDetail` to C# CQRS Handler

**Type:** Story
**Epic:** Rebuttal Workflow Action — SP Migration

## Description

Replace the dynamic-SQL stored procedure `spNGQMS_GetClientRebuttalAccountDetail` with a fully testable C# handler following the existing CQRS/Ardalis pattern. The handler, models, and pure business-logic layer are already written. Remaining work is the supporting infrastructure (specs, entities, repository factory extension, endpoint wiring, and tests).

---

## What Is Already Done

| Artifact | File | Status |
|---|---|---|
| Handler | `GetClientRebuttalAccountDetailHandler.cs` | Done |
| Models + Query | `ClientRebuttalAccountDetailModels.cs` | Done |
| Pure logic (counts, filters, joins) | `ClientRebuttalAccountDetailCommon.cs` | Done |

---

## Sub-Tasks & Logic Breakdown

### Sub-task 1 — Ardalis Specifications (8 new specs)
**Estimate: 3h**

Each spec mirrors one of the handler's `FetchXxx` methods:

| Spec Class | Table(s) Queried | Key Filter |
|---|---|---|
| `GetUploadTypeByAuditFormSpec` | `tblPGAuditForms → tblPGUploadTemplates → tblUploadTypes` | `AuditFormID` |
| `GetVerticalByAuditFormSpec` | `tblcodingdatabases → tblprojectgroups → tblpgauditforms` | `AuditFormID`, vertical IN ('FC','PC') |
| `GetClientFeedbackColumnsByAuditFormSpec` | `tblPGAuditForms → tblPGUploadTemplates → tblClientFeedbackColumns → tblUploadColumns` | `AuditFormID` |
| `GetAuditUploadsByAuditFormSpec` | `tblAuditUploads` | `AuditFormID` |
| `GetClientFeedbackAuditUploadAccountsSpec` | `tblAuditUploadAccounts` | `AuditUploadID IN (...)`, `IsError IN (errorValues)` |
| `GetClientAuditRebuttalsSpec` | `tblClientAuditRebuttals` | `AuditUploadAccountID IN (...)` |
| `GetClientAuditRebuttalHistoriesSpec` | `tblClientAuditRebuttalHistories` | `AuditUploadAccountID IN (...)` |
| `GetClientAuditRebuttalFeedbacksSpec` | `tblClientAuditRebuttalFeedbacks` | `AuditUploadAccountID IN (...)` |

---

### Sub-task 2 — Domain Entities (3 new, 2 likely exist)
**Estimate: 2h**

Entities needed by the specs and handler:

| Entity | Maps To | Columns Needed |
|---|---|---|
| `ClientAuditRebuttals` | `tblClientAuditRebuttals` | `AuditUploadAccountID`, `AssignedFrom`, `AssignedTo`, `ClientAuditRebuttalStatusID` |
| `ClientAuditRebuttalHistories` | `tblClientAuditRebuttalHistories` | Same 4 columns |
| `ClientAuditRebuttalFeedbacks` | `tblClientAuditRebuttalFeedbacks` | `AuditUploadAccountID`, `AssignedTo`, `HasRead` |
| `AuditUploads` | `tblAuditUploads` | `AuditUploadID`, `AuditFormID` — **likely already exists** |
| `AuditUploadAccounts` | `tblAuditUploadAccounts` | `AuditUploadAccountID`, `AuditUploadID`, dynamic feedback columns — **check existing** |

---

### Sub-task 3 — `IQMSClientDbRepositoryFactory` Schema-Name Overload
**Estimate: 1h**

The handler calls `qmsClientDbRepositoryFactory.CreateReadAsync<T>(qmsSchemaName)` with a raw schema
name string (not a `ProjectGroupID`). Check if this overload exists on the factory interface; if not,
add it. This is the only infrastructure change the handler needs.

---

### Sub-task 4 — HTTP Endpoint Wiring
**Estimate: 0.5h**

Register a GET/POST endpoint (matching the pattern of other rebuttal handlers in the project) that
maps request parameters to `GetClientRebuttalAccountDetailQuery` and invokes the handler. No logic
here — pure plumbing.

Parameters to map:

| Query Param | Type | Maps To |
|---|---|---|
| `auditFormID` | `int` | `AuditFormID` |
| `workedByID` | `int` | `WorkedByID` |
| `qmsSchemaName` | `string` | `QMSSchemaName` |
| `flag` | `int` | `Flag` |

---

### Sub-task 5 — Unit Tests for `ClientRebuttalAccountDetailCommon`
**Estimate: 2h**

The pure-logic class is fully side-effect-free, making it ideal for unit tests without any DB.
Cover the following scenarios:

- `ResolveMode(flag)` — all 4 flag values (0, 1, 2, 3)
- `ResolveSource(vertical, mode)` — FC/PC vs. ELSE branch, Feedback mode vs. others
- `BuildCounts(...)`:
  - Agent open count (AssignedFrom = 0, AssignedTo = workedByID)
  - Lead open count (AssignedFrom > 0, AssignedTo = workedByID)
  - Pending count (AssignedFrom = workedByID, AssignedTo = reportingAuthorityID)
  - Feedback count suppressed when `mode = AssignedClosed` (Flag = 2)
- `BuildAccountDetail(...)` — `AssignedTo` filter + mode-driven condition branch
- `MatchesRebuttalCondition` edge cases — null `AssignedFrom`, `ClientAuditRebuttalStatusID = 6`
- `MatchesFeedbackCondition` edge cases — null `FeedbackAssignedTo`, status != 6

---

### Sub-task 6 — Integration / Smoke Test
**Estimate: 1.5h**

Wire a test against a real (dev) database using the reference inputs from the SP file:

```
AuditFormID     = 2240
WorkedByID      = 37155
QMSSchemaName   = 'NexGenQMSParlallon.FCInpParallonIPCAC'
Flag            = 1
```

Verify that `AccountCountDetail` and `AccountDetail` match the SP output to confirm the port is
faithful.

---

## Total Estimation

| Sub-task | Estimate |
|---|---|
| Specifications (8 specs) | 3h |
| Entities (3 new) | 2h |
| Factory overload | 1h |
| Endpoint wiring | 0.5h |
| Unit tests | 2h |
| Integration smoke test | 1.5h |
| **Total** | **10h** |

---

## Acceptance Criteria

- [ ] All 8 specs created and registered with their repositories
- [ ] `ClientAuditRebuttals`, `ClientAuditRebuttalHistories`, `ClientAuditRebuttalFeedbacks` entities exist with required columns
- [ ] `IQMSClientDbRepositoryFactory.CreateReadAsync<T>(string schemaName)` overload available
- [ ] Handler builds and resolves via DI with no runtime errors
- [ ] Unit tests for `ClientRebuttalAccountDetailCommon` pass (all 4 modes, both verticals)
- [ ] Integration test: output matches SP output for reference inputs (`AuditFormID=2240`, `Flag=1`)
- [ ] SP is retired / marked deprecated after sign-off

---

## Implementation Order (Critical Path)

```
Entities  ──►  Specifications  ──►  Factory overload  ──►  Handler runs end-to-end
                                                              │
                                                              ├──►  Endpoint wiring
                                                              └──►  Tests (unit + integration)
```

Tests can be written in parallel with entity/spec work once the models are finalised.

---

## Reference

- SP source: `spNGQMS_GetClientRebuttalAccountDetail.sql`
- Handler: `GetClientRebuttalAccountDetailHandler.cs`
- Models: `ClientRebuttalAccountDetailModels.cs`
- Business logic: `ClientRebuttalAccountDetailCommon.cs`
