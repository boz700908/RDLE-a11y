#!/bin/bash
# RDMods 一键发布脚本
# 编译 Release 版本并复制到 release 文件夹

set -e  # 遇到错误立即退出

echo "=========================================="
echo "RDMods 一键发布脚本"
echo "=========================================="
echo ""

# 从 Directory.Build.user.props 提取游戏路径（若存在）
GAME_DIR=""
if [ -f "Directory.Build.user.props" ]; then
  GAME_DIR=$(grep '<GameDir>' Directory.Build.user.props | sed 's/.*<GameDir>\(.*\)<\/GameDir>.*/\1/' | tr -d '\r')
fi

if [ -z "$GAME_DIR" ]; then
  echo "错误：请先创建 Directory.Build.user.props 并配置 GameDir"
  echo "参考 Directory.Build.user.props.example 文件"
  exit 1
fi

RELEASE_DIR="release/main"

# 从 docs/changelog-en.txt 第一行提取版本号
VERSION=$(head -1 docs/changelog-en.txt | sed 's/ .*//' | tr -d '\r')
if [ -z "$VERSION" ]; then
  echo "错误：无法从 docs/changelog-en.txt 提取版本号"
  exit 1
fi
echo "当前版本: $VERSION"
echo ""

# 1. 清理并编译 Release 版本
echo "[1/6] 清理旧的编译产物..."
dotnet clean RDLE-a11y.sln -c Release

echo ""
echo "[2/6] 编译 Release 版本..."
dotnet build RDLE-a11y.sln -c Release

# 2. 复制编译产物到 release 文件夹
echo ""
echo "[3/6] 复制编译产物到 release 文件夹..."

# 创建目标目录（如果不存在）
mkdir -p "$RELEASE_DIR/BepInEx/plugins"

# 复制 Mod DLL
if [ -f "$GAME_DIR/BepInEx/plugins/RDLevelEditorAccess.dll" ]; then
    cp "$GAME_DIR/BepInEx/plugins/RDLevelEditorAccess.dll" "$RELEASE_DIR/BepInEx/plugins/"
    echo "  ✓ 已复制 RDLevelEditorAccess.dll"
else
    echo "  ✗ 错误: 找不到 RDLevelEditorAccess.dll"
    exit 1
fi

# 复制 Helper EXE
if [ -f "$GAME_DIR/RDEventEditorHelper.exe" ]; then
    cp "$GAME_DIR/RDEventEditorHelper.exe" "$RELEASE_DIR/"
    echo "  ✓ 已复制 RDEventEditorHelper.exe"
else
    echo "  ✗ 错误: 找不到 RDEventEditorHelper.exe"
    exit 1
fi

# 3. 复制文档到 release 文件夹
echo ""
echo "[4/6] 复制文档到 release 文件夹..."

# 创建 docs 目录（如果不存在）
mkdir -p "release/docs"

# 复制所有文档文件
if [ -d "docs" ]; then
    cp -r docs/* release/docs/
    echo "  ✓ 已复制用户手册文档"
else
    echo "  ⚠ 警告: docs 文件夹不存在，跳过文档复制"
fi

# 4. 打包发布压缩包
echo ""
echo "[5/6] 打包发布压缩包..."

RELEASE_ZIP="release/RDLE-Access-Release.zip"
rm -f "$RELEASE_ZIP"
powershell.exe -NoProfile -Command "Compress-Archive -Path 'release/docs','release/main' -DestinationPath '$RELEASE_ZIP'"
echo "  ✓ 已创建 $RELEASE_ZIP"

# 5. 归档版本压缩包
echo ""
echo "[6/6] 归档版本压缩包..."

ARCHIVE_DIR="release/archive"
mkdir -p "$ARCHIVE_DIR"
cp "$RELEASE_ZIP" "$ARCHIVE_DIR/RDLE-Access-${VERSION}.zip"
echo "  ✓ 已归档 $ARCHIVE_DIR/RDLE-Access-${VERSION}.zip"

# 完成
echo ""
echo "=========================================="
echo "✓ 发布完成！"
echo "=========================================="
echo ""
echo "发布文件位置: $RELEASE_DIR"
echo "发布压缩包:   $RELEASE_ZIP"
echo "归档压缩包:   $ARCHIVE_DIR/RDLE-Access-${VERSION}.zip"
echo ""
echo "包含文件:"
echo "  - BepInEx/plugins/RDLevelEditorAccess.dll (Mod)"
echo "  - RDEventEditorHelper.exe (Helper)"
echo "  - ../docs/ (用户手册)"
echo ""
