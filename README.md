# Tenant Work Portal — portfolio demonstration

Original synthetic demonstration prepared for HCVT's [Staff Software Engineer, Application Modernization](https://jobs.lever.co/hcvt/33a31935-4b14-4699-821a-a2e618f24358) opening in Irvine. This is **not** HCVT work or a production system. It contains no employer data, code, credentials, or private documents.

The posting emphasizes one client-facing front door, a canonical client/engagement/task model, tenant isolation, integration adapters, idempotent write-back, and reconciliation. This small .NET 10 API demonstrates a limited slice: tenant-scoped tasks, idempotent writes to a simulated downstream service, and drift detection. It does not implement a web front door, fine-grained engagement permissions, a real third-party adapter, or Azure deployment.

## Run

```sh
dotnet run --project TenantPortal.csproj --urls http://127.0.0.1:5087
```

Example: `curl -H 'X-Actor-Id: alice' http://127.0.0.1:5087/tenants/alpha/tasks`

`POST /tenants/{tenant}/tasks/{taskId}/sync` also needs an `Idempotency-Key` header. `GET /tenants/{tenant}/reconciliation` lists tasks whose local state differs from the synthetic downstream state. `alice` belongs only to `alpha`; `bob` belongs only to `beta`; `admin` belongs to both. The actor header is **only a local simulation**. Production authentication would validate OIDC tokens and enforce identity and authorization in a durable store.

## Verify

```sh
python3 verify.py
```

The integration checks cover tenant isolation, missing idempotency keys, replay, conflicting key reuse, and reconciliation after sync. The service intentionally uses in-memory state to remain dependency-free. Restarting it resets all data; it does not claim Azure, SOC 2, or production readiness.
