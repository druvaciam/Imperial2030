# Agent Rules

- **Cleanup Temporary Files:** Always proactively remove any temporary scripts (e.g., Python scripts, text files) or intermediate media files (like extracted PNGs) created during debugging or troubleshooting before concluding a task.
- **Backend Bugfixes (Test-Driven):** When fixing a bug on the backend, always write a failing test first to reproduce the issue, verify that it fails, then implement the code fix, and finally run the test again to ensure it passes (green).
- **Cross-Platform Compatibility:** Ensure all written code (including scripts and file paths) is fully compatible and works seamlessly across both Windows and Linux operating systems.
- **Build Before Completion:** Always compile/build the project to ensure there are no compilation errors before reporting that a task is done.
