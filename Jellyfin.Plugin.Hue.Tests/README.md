# Jellyfin Plugin Hue - Tests

This directory contains unit tests for the Jellyfin Plugin Hue project.

## Test Structure

- **ColorProcessingTests.cs** - Tests for RGB/HSL color conversion algorithms
- **PluginConfigurationTests.cs** - Tests for plugin configuration validation

## Running Tests

### Command Line

```bash
# Run all tests
dotnet test

# Run tests with detailed output
dotnet test --verbosity detailed

# Run tests with code coverage
dotnet test --collect:"XPlat Code Coverage"

# Run specific test class
dotnet test --filter FullyQualifiedName~ColorProcessingTests

# Run specific test method
dotnet test --filter FullyQualifiedName~ColorProcessingTests.RgbToHsl_ConvertsCorrectly
```

### Visual Studio / Rider

1. Open the solution
2. Navigate to Test Explorer
3. Run All Tests or select specific tests

## Test Coverage

The test suite covers:

- Color space conversion (RGB ↔ HSL)
- Configuration validation for all settings
- Edge cases and boundary conditions
- Round-trip conversions

## CI/CD Integration

Tests are automatically run on:
- Push to main/master/develop branches
- Pull requests
- Manual workflow dispatch

See `.github/workflows/dotnet-ci.yml` for the full CI/CD configuration.

## Adding New Tests

When adding new tests:

1. Create test files in this directory
2. Use xUnit framework
3. Follow naming convention: `[ClassName]Tests.cs`
4. Use descriptive test method names following pattern: `Method_Scenario_ExpectedResult`
5. Add appropriate assertions and test data

## Test Dependencies

- **xUnit** - Test framework
- **Moq** - Mocking library for dependencies
- **coverlet.collector** - Code coverage tool
