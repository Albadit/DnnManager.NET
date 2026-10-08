# Maintainability: smells and metrics

## Code Smells Reference

| Smell | Indicator | Remedy | Impact |
|-------|-----------|--------|--------|
| **Long Method** | > 20 lines, multiple concerns | Extract methods with descriptive names | High |
| **God Class** | Too many responsibilities, > 200 lines | Split into focused classes (SRP) | High |
| **Feature Envy** | Method uses another class's data excessively | Move method to that class | High |
| **Divergent Change** | One class changed for multiple unrelated reasons | Separate concerns into distinct classes | High |
| **Shotgun Surgery** | One change requires edits in many files | Consolidate related logic into one place | High |
| **Duplicate Code** | Copy-pasted logic across modules | Extract shared utility/base class | High |
| **Primitive Obsession** | Raw primitives instead of domain types | Create value objects | Medium |
| **Data Clumps** | Same field group repeated across methods/classes | Extract a class for the group | Medium |
| **Long Parameter List** | > 3-4 parameters | Introduce parameter objects or builders | Medium |
| **Magic Numbers/Strings** | Unexplained literal values in code | Named constants or enums | Medium |
| **Dead Code** | Unreachable or unused code/files | Remove (VCS preserves history) | Low |
| **Commented-Out Code** | Lingering code fragments | Remove (VCS preserves history) | Low |
| **Speculative Generality** | Unused abstractions "for the future" | Remove unused abstractions | Low |

## Quality Metrics Targets

| Metric | Target | Finding Threshold |
|--------|--------|-------------------|
| **Cyclomatic Complexity** | < 10 per method | > 20 = HIGH |
| **Cognitive Complexity** | < 15 per method | > 25 = HIGH |
| **Method Length** | < 20 lines | > 30 = MEDIUM |
| **Class Length** | < 200 lines | > 300 = HIGH |
| **Nesting Depth** | ≤ 3 levels | > 4 = MEDIUM |
| **Parameter Count** | ≤ 4 parameters | > 6 = MEDIUM |
| **Test Coverage (critical paths)** | > 80% | < 50% = HIGH |
| **Duplication Ratio** | < 3% | > 5% = MEDIUM |
