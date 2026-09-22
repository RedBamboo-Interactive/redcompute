# Decision capability and Kev local provider

RedCompute exposes provider-neutral typed decisions at POST /decision/generate. The first provider identity is kev-local-quality; callers should depend on the decision contract, not on Kev-specific routes.

A decision result is advisory. It grants no permission, capability, approval, or authority to perform an action. actionAuthority is always false.

## Contract

The request contains one bounded state value and a map of independently evaluated questions:

    {
      "state": {"subject": "Charged twice", "body": "The parcel is also two weeks late."},
      "questions": {
        "route": {
          "type": "choice",
          "instructions": "Which team should handle the primary issue?",
          "criteria": {"billing": "Charges and refunds", "shipping": "Late deliveries"}
        },
        "urgent": {"type": "noul", "instructions": "Does this require urgent attention?"},
        "severity": {
          "type": "score",
          "instructions": "How severe is the issue?",
          "criteria": ["low", "medium", "high"]
        }
      }
    }

- Choice selects one named, unordered option and returns the complete option distribution.
- Score returns the expected zero-based position on an ordered rubric, its legend, and the complete level distribution.
- Noul returns the probability that a proposition is true. RedCompute also supplies the equivalent false/true distribution.
- Choice and Score confidence is distribution concentration. It is not a measured probability that the answer is correct.
- Unknown fields, wrong discriminators, wrong criteria shapes, excessive state or question sizes, and option/question limits fail with precise 422 validation_failed field paths before inference.

The response includes provider, model, modelRevision, calibrationRevision, queueMs, modelMs, and totalMs where available. Sync requests use the default job path; ?async=false remains synchronous. ?async=true or X-Async: true returns 202 and the normal job id. Signed provenance, idempotency, cancellation, job output, and rerun handling are the same as other RedCompute capabilities.

Discovery surfaces the full discriminated JSON schemas at GET /discover, GET /openapi.json, and GET /decision/contract. Detailed operations are GET /decision/status and POST /decision/validate.

/decision/validate is covered by the normal RedCompute authentication policy and performs one synchronous, bounded, action-free validation through the real provider path. Every admitted invocation creates exactly one normal durable job with signed provenance for the validate route. The response contains jobId plus the validation result; that same JSON is persisted as the job result and is observable through the canonical job endpoints. Provider failures and cancellation are terminal job states. Invalid authentication, provenance, or other pre-admission input creates no job. Validation also runs a bounded six-order Choice permutation diagnostic when the sidecar supports /v1/systemone/permute. argmax_stable false is important diagnostic information about the question/options, not a provider failure.

Raw state is persisted only as normal audited job input. The provider and ordinary information logs do not print it.

## Default provider configuration

The shipped default is deliberately attach-only: it probes a private-local endpoint and does not install packages, download weights, or launch a process.

    {
      "Type": "KevLocalQuality",
      "Endpoint": "http://127.0.0.1:8008",
      "Model": "kev-latest",
      "ModelRevision": "2629c06a5aeb0feb3b9783bafed17ed8f39ecf5c",
      "BaseModelRevision": "68c46c4b3498877f3ef123c856ecfde50c39f404",
      "CalibrationRevision": "temperature-2.2973967099940698",
      "TimeoutSeconds": 120,
      "StartupTimeoutSeconds": 180,
      "WslDistro": "Ubuntu-24.04",
      "MergeLora": false,
      "Dtype": "bfloat16",
      "KernelBackend": "flash-qla-sm120"
    }

On the measured RTX 5090 configuration, MergeLora=false is a production requirement. Kev's default merge path merges LoRA in fp32 before casting and OOMs on the 32 GB card. When an explicit LaunchCommand is configured, RedCompute forces KEV_MERGE=0 and KEV_DTYPE=bfloat16 into the owned process environment. Status reports configured merge, dtype, and kernel settings separately from sidecar-observed run, device, dtype, and temperature so attach-only status does not pretend RedCompute controls an external process.

ModelRevision is the immutable Hugging Face adapter snapshot commit used in the verified run: 2629c06a5aeb0feb3b9783bafed17ed8f39ecf5c. BaseModelRevision records the immutable Qwen/Qwen3.5-9B-Base snapshot pinned by that adapter's provenance: 68c46c4b3498877f3ef123c856ecfde50c39f404. Neither value is a date, branch, or mutable tag.


A configured launch command is opt-in. WSL launches receive a new process group, and RedCompute records its Linux PID plus /proc start time. Stop verifies that identity and signals only that process group; native launches use the exact Windows process tree. There is no name-based pkill and no operation can target STT, Ollama, ComfyUI, or an unrelated WSL process.

The provider does not copy inbound request headers. In particular, the signed RedLeaf bearer and legacy provenance headers never reach the unauthenticated sidecar.

## Readiness and capacity

GET /decision/status distinguishes:

- not-ready: the configured endpoint is unreachable;
- process-ready: /v1/models succeeds but this provider has not completed inference;
- inference-warmed: at least one inference completed successfully.

The first optimized request may compile kernels, so process-ready is not latency-ready.

Kev-9B added about 10.7 GB resident GPU memory in the measured setup: total use was 16.8 GB, up from 6.1 GB with local STT. It could not safely coexist with the prior roughly 22 GB ComfyUI residency. Upstream 409, 423, 507, resource_busy, or CUDA OOM responses map to the typed 409 resource_busy error. This phase never evicts another model. Release the conflicting workload explicitly and retry.

## Manual RTX 5090 WSL/CUDA smoke and benchmark

This procedure is manual and bounded. It assumes a separately approved, already provisioned local Kev checkout and local Kev-9B checkpoint. It must not point --run at a Hub id unless the operator has separately authorized the model download.

Measured baseline:

- WSL Ubuntu 24.04
- Python 3.12.3
- PyTorch 2.8.0+cu128
- RTX 5090, CUDA capability (12, 0)
- flash-linear-attention 0.5.2
- flash-qla 0.1.2, providing the SM120 GDN backend
- Kev dtype bfloat16
- served temperature 2.2973967099940698

1. Verify the environment without changing it:

       python --version
       python -c "import torch; print(torch.__version__, torch.cuda.get_device_name(), torch.cuda.get_device_capability())"
       python -c "import fla, flash_qla; print('optimized kernels import successfully')"
       nvidia-smi

2. Ensure the ComfyUI model is explicitly released before loading Kev-9B. Do not stop STT and do not run a broad process-name kill. Confirm the post-release baseline with nvidia-smi. The observed STT-inclusive baseline was about 6.1 GB.

3. Start the already provisioned checkpoint as one exact user service/cgroup:

       systemd-run --user --unit=kev-9b --collect --setenv=KEV_MERGE=0 --setenv=KEV_DTYPE=bf16 uv run --extra serve python -m kev.serve --run /absolute/local/path/to/kev-9b --port 8008

   Inspect only that unit with systemctl --user status kev-9b.service and journalctl --user-unit kev-9b.service. Verify GET /v1/models reports the intended run, CUDA device, bfloat16, and expected temperature. Then verify /decision/status is process-ready.

4. Send one mixed Choice+Noul+Score request through POST /decision/generate. Treat this as warm-up, not a benchmark sample. The first optimized request took about 12 seconds while kernels compiled in the measured run. Verify /decision/status changes to inference-warmed.

5. Send two additional unmeasured warm-up requests, then time ten identical three-question requests. Record client wall time and response timing.modelMs; report mean, median, and p95 with state byte size and question count. Excluding the first two requests, the measured ten-run average was approximately 125 ms for wall and model time.

6. Exercise structured state, all question types, strict invalid-input 422, packed versus separate behavior, and a six-order permutation run. Record argmax_stable and per-option spread. The measured deliberately ambiguous multi-issue ticket was not stable across six orders; report that as question sensitivity, not provider downtime or validation failure.

7. Record nvidia-smi after load. The measured total was 16.8 GB, about 10.7 GB above the STT-inclusive baseline. If capacity is unavailable, confirm RedCompute returns resource_busy and does not stop ComfyUI, STT, Ollama, or any other process.

8. Stop only the exact unit:

       systemctl --user stop kev-9b.service
       systemctl --user reset-failed kev-9b.service

   Confirm the unit is gone and GPU memory returns to baseline. Restore ComfyUI explicitly through its normal RedCompute control path if the test required releasing it. Confirm STT and ComfyUI independently report Running.

No model provisioning, deployment, RedCompute restart, or rebuild is part of this procedure.
