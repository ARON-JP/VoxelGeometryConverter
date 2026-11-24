# Voxel Geometry Converter

Bedrock の `poly_mesh` を、ブロックで描画できるキューブ群に変換する Windows 用ツールです。入力モデルは変更しません。

## 使い方

`VoxelGeometryConverter.exe --settings settings.example.json`

設定ファイル内のパスは、設定ファイル自身のフォルダーを基準にできます。`quality` は `low` (48)、`medium` (64)、`high` (96)、`ultra` (128) です。`resolution` を指定すると品質指定より優先されます。

出力後は、表示するブロック JSON の `minecraft:geometry` に `outputGeometryId`、`minecraft:material_instances.*.texture` に `textureShortName` を指定してください。出力テクスチャを `terrain_texture.json` に同じ short name で登録します。

## 設定項目

- `inputGeometry` / `inputTexture`: 変換元の `.geo.json` と PNG。
- `outputGeometry` / `outputTexture`: 生成する geometry JSON と不透明カラー atlas の出力先。
- `sourceGeometryId`: 1 ファイルに複数の geometry がある場合の変換元 ID。省略時は最初のポリメッシュを選択します。
- `outputGeometryId`: 生成後にブロック JSON から参照する geometry ID。
- `textureShortName`: `terrain_texture.json` とブロック JSON で使うテクスチャの短縮名。
- `quality` または `resolution`: 形状精度。高いほどキューブ数・ファイルサイズ・読み込み負荷が増えます。
- `atlasSize` / `tileSize`: 出力色 atlas の寸法と、1 キューブに割り当てる色タイルの大きさ。

ブロック JSON の設定例:

```json
"minecraft:geometry": "geometry.paper_bag_voxel",
"minecraft:material_instances": {
  "*": { "texture": "paper_bag_voxels", "render_method": "opaque" }
}
```
