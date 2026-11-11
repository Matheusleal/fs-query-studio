# Delta Specification: Increase Integration Tests Timeout & Resiliency for Windows CI

## Requirements (GEARS)

### MODIFIED Requirements

#### REQ-CI-001: Integration Tests Process Communication Timeout
**While** running integration tests against a spawned child worker process,  
**when** dispatching a request via `roundTrip`,  
the test framework shall allow up to 120 seconds for standard output response frames before triggering cancellation.

#### REQ-CI-002: Windows CI Job Execution Budget
**Where** executing the Windows CI test suite on GitHub-hosted runners,  
the workflow runner shall allocate a 20-minute timeout budget to accommodate concurrent solution testing and FSI compiler initialization under constrained CPU resources.

