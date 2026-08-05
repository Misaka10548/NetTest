# NetTest

纯vibe写的一个连通性检测，有多少用我不知道，但是写出来了（）

Codex GPT-5.6 Sol (xhigh)写的计划文件[项目计划](./Plan.md)、[系统设计](./SystemDesign.md)、[技术规范](./TechnicalSpecification.md)；deepseek-v4-flash-0731完成落地

导出的数据可能需要转换器展平一下，所以vibe了[这个](./Convert/)

~~由于dsflash笨笨操作没弄好git交了一些垃圾（其实是我偷懒没写.gitignore）所以只有一个Commit~~

整个项目纯AI不含人工

然后你只需要这样再打包就能随便拿走用了（大概？

```shell
dotnet publish -r win-x64 --self-contained
```
