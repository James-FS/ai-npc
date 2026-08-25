@echo off
rem AI NPC Server 启动脚本
rem 说明：DeepSeek / GLM（open.bigmodel.cn）国内直连，无需代理，直接启动即可。
rem 若要用 OpenCode Zen（opencode.ai，含 Ox Alpha）等境外端点，需先开代理，
rem 并取消下面一行的注释（端口按你的代理软件修改）：
rem set AIBot_HTTP_PROXY=http://127.0.0.1:7890
cd /d %~dp0src\AIBot.Server
dotnet run
