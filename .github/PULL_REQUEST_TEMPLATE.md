## 改动内容

<!-- 这个拉取请求做了什么、为什么。一个拉取请求只做一件事。关联的 Issue 写成 Closes #123。 -->

## 验证方式

<!-- 新增或修改了哪些测试；手动验证过的平台与步骤。 -->

## 检查清单

- [ ] 已运行 `./scripts/verify.sh` 或 `./scripts/verify.ps1`（Release 构建、全部测试、`git diff --check`）
- [ ] 新功能或缺陷修复有对应的测试；平台专有行为使用 `WindowsFact` / `UnixFact` 等特性
- [ ] 所有新增的类型与成员都有中文 XML 文档注释，一个文件只声明一个类型
- [ ] 用户可见的变化已更新相关文档与 `CHANGELOG.md` 的“未发布”部分
- [ ] 持续集成在 Windows、Ubuntu 与 macOS 上都已通过
