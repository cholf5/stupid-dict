# 审查范围

**关联计划**: docs/plans/2026-10-20-cloud-translation.md
**开始时间**: 1792453311

## 预期修改文件

### 文档

- docs/plans/2026-10-20-cloud-translation-design.md
- docs/plans/2026-10-20-cloud-translation.md
- docs/plans/2026-10-20-cloud-translation-review-scope.md
- README.md
- AGENTS.md

### 新增源码

- src/StupidDict.App/Translation/TranslationTypes.cs
- src/StupidDict.App/Translation/TranslationEndpoint.cs
- src/StupidDict.App/Translation/TranslationText.cs
- src/StupidDict.App/Translation/TranslationPrompt.cs
- src/StupidDict.App/Translation/TranslationQualityGate.cs
- src/StupidDict.App/Translation/TranslationCache.cs
- src/StupidDict.App/Translation/ITranslationService.cs
- src/StupidDict.App/Translation/TranslationService.cs
- src/StupidDict.App/MainWindow.Translation.cs

### 修改源码

- src/StupidDict.App/MainWindow.axaml.cs
- src/StupidDict.App/Settings/AppSettings.cs
- src/StupidDict.App/Settings/SettingsWindow.axaml
- src/StupidDict.App/Settings/SettingsWindow.axaml.cs
- src/StupidDict.App/App.axaml.cs
- src/StupidDict.App/AppPaths.cs
- src/StupidDict.App/AppLocations.cs
- src/StupidDict.App/Localization/Translations.cs

### 新增测试

- tests/StupidDict.App.Tests/TranslationEndpointTests.cs
- tests/StupidDict.App.Tests/TranslationTextTests.cs
- tests/StupidDict.App.Tests/TranslationCacheTests.cs
- tests/StupidDict.App.Tests/TranslationServiceTests.cs
- tests/StupidDict.App.Tests/TranslationFlowTests.cs

### 修改测试

- tests/StupidDict.App.Tests/HeadlessTests.cs
- tests/StupidDict.App.Tests/DownloadFlowTests.cs
- tests/StupidDict.App.Tests/SettingsServiceTests.cs

## 设计文档

- docs/plans/2026-10-20-cloud-translation-design.md
