# Changelog

All notable changes to this project are documented here.
Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versions follow [SemVer](https://semver.org/).

## [Unreleased]

### Added

- Repository scaffold: solution, packaging, CI, release workflow.
- `IsTemporal()` fluent configuration writing `Hindsight:*` annotations.
- `UseHindsight()` on `DbContextOptionsBuilder`: enables the model-finalizing convention that
  generates a property-bag history entity type for every `IsTemporal()` entity, so
  `dotnet ef migrations add` creates the history table alongside the main table. The history table
  carries the versioned columns (excluded properties omitted, every column nullable), the period
  columns `valid_from` / `valid_to`, the change-context columns
  (`operation`, `changed_by`, `changed_by_name`, `correlation_id`, `reason`, `extra`), an identity
  `history_id` primary key, and a `(key…, valid_from desc)` index.
- Model validation: a temporal entity without a primary key throws `InvalidOperationException`; a
  temporal entity in a TPH hierarchy throws `NotSupportedException`.
