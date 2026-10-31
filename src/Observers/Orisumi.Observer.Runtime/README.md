# Orisumi.Observer.Runtime

Orisumi.Observer.Runtime 运行 Observer 的进程，把它们交来的批次形成版本并提交到后端。设备上由客户端启动，服务器上以服务方式运行。职责和协议见 [Hosting · Runtime](../../Docs/Orisumi.Docs/content/docs/hosting/runtime.mdx) 和 [Hosting · Server](../../Docs/Orisumi.Docs/content/docs/hosting/server.mdx)。

## 构建安装包

`orisumi observers pack` 按平台发布 Runtime，并构建给出的 Observer 的安装包：

```sh
orisumi observers pack application-identity --target osx-arm64 --output <输出目录>
```

`<输出目录>/runtime/` 是 Runtime，`<输出目录>/bundled/` 是安装包。其他平台换成对应的 RID，例如 `linux-x64`。清单 `observer.json` 的 `id`、`name` 和 `scope` 取自 Observer 项目的 `ObserverPackageId`、`ObserverName` 和 `ObserverScope`，版本和入口取自项目的 `Version` 和 `AssemblyName`。

## 在本地以服务方式运行

1. 启动环境，读取入口地址：

   ```sh
   orisumi env up web --temp
   orisumi env status --json
   ```

2. 在 Web 控制台的“托管”页点“接入”，得到配对码。
3. 启动 Runtime：

   ```sh
   <输出目录>/runtime/Orisumi.Observer.Runtime --service --data-dir <持久目录> --backend <API 地址>/ --bundled <输出目录>/bundled --pairing-code <配对码> --name <名称>
   ```

之后的启动省略 `--pairing-code`。Runtime 安装附带的应用识别包，和其他 Observer 一样管理；它的模型型号和间隔在“托管”页修改设置。`OPENAI_API_KEY` 从启动 Runtime 的环境继承。

## 容器

先为镜像的平台打包，再以输出目录为构建上下文构建镜像：

```sh
orisumi observers pack application-identity --target linux-arm64 --output <输出目录>
docker build -f src/Observers/Orisumi.Observer.Runtime/Dockerfile -t orisumi-runtime <输出目录>
```

镜像以非 root 用户运行，持久目录是 `/data`，附带输出目录中的安装包。运行时挂载 `/data` 的卷，用 `--backend` 给出 API 地址，第一次启动时加 `--pairing-code`。API 不在本机时用 HTTPS。

## 验证

`tests/Orisumi.Integration.Tests/` 中的 `RuntimeManagementTests` 和 `Runtime*Tests` 检查同步、提交和读取，Case 见 [Testing](../../Docs/Orisumi.Docs/content/docs/testing/index.mdx)。
