SP Toon Shader for 3ds Max 2026 v7 - Fixed

１．MAXのマテリアルエディタでOSLを検索して読み込み
２．OSLコードに　SP_Toon_User0_3dsMax2026_v7.osl　を読み込み
<img width="1336" height="907" alt="img_max1" src="https://github.com/user-attachments/assets/662bb46d-c03b-46b2-addc-2007126fb245" />
３．ArnoldのStanderdSurfaceを読み込み
４．OutColorとeMission_colorをつなぎます。
５．BaseColorとSpecularの数値は0.0、Emissionは1.0にします。
<img width="1482" height="905" alt="img_max2" src="https://github.com/user-attachments/assets/308072e7-926d-43e2-94b4-d59eea4c80fa" />
６．スクリプト＞SP_Toon_Controls_3dsMax2026_v7.py を開いて実行（ツール＞すべて評価）
７．OSLマテリアルを選択した状態で Load Selected OSL Map を押す
<img width="1639" height="1011" alt="img_max3" src="https://github.com/user-attachments/assets/512dc5b0-a258-4f8e-90ad-836f35582fc2" />
８．curvatureマップを割り当てるとInlineに反映します。
<img width="1300" height="1008" alt="img_max4" src="https://github.com/user-attachments/assets/faefb39f-65a2-49ec-93c1-ac3da37d7367" />

---------------------------------------------------

SP Toon Shader for 3ds Max 2026 v7 - Fixed
Arnold OSL & Python Control UI

Changelog:
- Fixed raw string literal issue in Python UI source code.
- Added 'InlineOnTop' parameter to dynamically control overlay order between Outline and Inline.
- Reordered UI layout so that InlineOnTop sits right below Inline Blur.
- Synced all default colors and toon gradient palettes with Blender 5 v3.1.3 preset values.
- Completely preserved English/Japanese bilingual dictionaries inside the Python UI code.
