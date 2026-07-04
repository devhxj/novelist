# Release Notes

### 2026-07-04

- Novelist 桌面版启动更直接，不再依赖本机 ASP.NET/Kestrel 服务作为产品运行时，安装包启动路径更轻量。
- 封面显示改为通过桌面桥接读取，打包后的应用不再需要 `/covers` HTTP 路由也能显示小说封面。
- 开发模式仍可通过 Vite 地址调试前端，桌面业务调用继续走 Photino bridge。
