# ESP32-S3-Touch-AMOLED-2.16

URL: https://docs.waveshare.net/ESP32-S3-Touch-AMOLED-2.16/

[![ESP32-S3-Touch-AMOLED-2.16](https://docs.waveshare.net/assets/images/ESP32-S3-Touch-AMOLED-2.16-showcase-174b30e4b87190a8ea342215849d233f.webp)](https://www.waveshare.net/shop/ESP32-S3-Touch-AMOLED-2.16.htm)

本产品是一款微雪 (Waveshare) 设计的高性能、高集成的微控制器开发板。在较小的板型下，板载了 2.16 英寸电容高清 AMOLED 屏、高度集成的电源管理芯片、六轴传感器 （三轴加速度计与三轴陀螺仪）、RTC、低功耗音频编解码芯片和回声消除电路等外设，方便开发并嵌入到产品中。

**如果你在寻找：**

- **使用 AI 工具编程** ：点击 **发给 AI** 并选择 AI 工具，询问与本页面相关的问题；也可以选择 **复制为 Markdown** ，将页面内容复制并粘贴到 AI 工具中辅助编程。
- **原理图、规格书和示例代码** ：参见 [相关资料](https://docs.waveshare.net/ESP32-S3-Touch-AMOLED-2.16/Resources-And-Documents)
- **出厂固件** ：参见 [出厂固件使用](https://docs.waveshare.net/ESP32-S3-Touch-AMOLED-2.16/Instructions-For-Use)
- **Arduino 开发** ：参见 [Arduino 开发](https://docs.waveshare.net/ESP32-S3-Touch-AMOLED-2.16/Arduino)
- **ESP-IDF 开发** ：参见 [ESP-IDF 开发](https://docs.waveshare.net/ESP32-S3-Touch-AMOLED-2.16/ESP-IDF)
- **问题排查** ：参见 [产品 FAQ](https://docs.waveshare.net/ESP32-S3-Touch-AMOLED-2.16/FAQ) 或联系 [技术支持](https://docs.waveshare.net/ESP32-S3-Touch-AMOLED-2.16/Technical-Support)

| SKU | 产品 |
| --- | --- |
| 33969 | ESP32-S3-Touch-AMOLED-2.16 |
| 33970 | ESP32-S3-Touch-AMOLED-2.16-EN |

## 板载资源

![ESP32-S3-Touch-AMOLED-2.16 板载资源](https://docs.waveshare.net/assets/images/ESP32-S3-Touch-AMOLED-2.16-details-intro-37e2fc2230fbf000705ffe5579f04f95.webp)

1. **ESP32-S3R8** Wi-Fi 和蓝牙 SoC，240MHz 运行频率，叠封 8MB PSRAM
2. **16MB NOR Flash**
3. **AXP2101** 高集成度的电源管理芯片
4. **板载贴片天线** 支持 2.4GHz Wi-Fi (802.11 b/g/n) 和 Bluetooth 5 (LE)
5. **Type‑C 接口** ESP32‑S3 USB 接口，用于烧录程序和日志打印
6. **MX1.25 锂电池接口** MX1.25 2PIN 连接器，可用于接入 3.7V 锂电池，支持充放电
7. **板载 IPEX 1 代天线座** 通过切换电阻可选择外接天线
8. **Micro SD 卡槽**
9. **双麦克风设计** 配合回声消除电路，能够更高质量地采集音频
10. **GPIO 18 按键** 支持自定义功能
11. **PWR 电源按键** 可控制电源通断，支持自定义功能
12. **BOOT 按键** 用于设备启动和功能调试
13. **QMI8658** 六轴惯性测量单元 (IMU)，包含一个 3 轴陀螺仪和一个 3 轴加速度计
14. **屏幕接口**

### 外设速查

| 外设 | 器件 / 功能 | 接口 | 主要引脚 | 说明 |
| --- | --- | --- | --- | --- |
| 主控 | ESP32-S3R8 | - | - | ESP32-S3，内置 PSRAM |
| 显示屏 | 2.16 寸 AMOLED，CO5300 驱动 | QSPI | `GPIO4/5/6/7/38/12/39` | `480 x 480` ，QSPI 四线数据 |
| 触摸 | CST9220 | I2C + INT + RST | `GPIO14/15/11/40` | 与 RTC、IMU、PMU、音频 Codec 共用 I2C |
| 电源管理 | AXP2101 | I2C | `GPIO14/15` | 负责电源管理、电池/USB 供电检测等 |
| IMU | QMI8658A / QMI8658C | I2C + INT | `GPIO14/15/17/21` | 6/9 轴传感器， `QMI_INT1` 、 `QMI_INT2` 分别接 `GPIO17` 、 `GPIO21` |
| RTC | PCF85063ATL | I2C + INT | `GPIO14/15/13` | RTC 中断接 `GPIO13` |
| microSD | SD 卡座 | SD SPI | `GPIO1/2/3/41` | BSP 使用 `CMD=GPIO1` 、 `CLK=GPIO2` 、 `D0=GPIO3` ；原理图同时标注 `SDCS=GPIO41` |
| 音频 Codec | ES8311 | I2C + I2S | `GPIO8/9/42/45/46` | 扬声器播放， `GPIO46` 为功放控制 |
| 麦克风 ADC | ES7210 | I2C + I2S | `GPIO9/10/42/45` | 双数字麦克风阵列采集 |
| I2C 总线 | 板载共享 I2C | I2C | `GPIO14=SCL` 、 `GPIO15=SDA` | 连接触摸、AXP2101、QMI8658、RTC、Codec 等 |
| USB | USB D-/D+ | USB | `GPIO19/20` | ESP32-S3 原生 USB |
| 串口 | UART0 | UART | `GPIO43/44` | `GPIO43=U0TXD` 、 `GPIO44=U0RXD` |
| 按键 / 系统输出 | BOOT、CHIP_PU、GPIO18、PWRON、SYS_OUT | GPIO / PMU | `GPIO0/18/16` | `GPIO18` 接 Key3； `GPIO16` 为原理图标注的 `SYS_OUT` |

## 引脚定义

### GPIO 引脚分配

| GPIO | 功能 | 连接对象 / 备注 |
| --- | --- | --- |
| `GPIO0` | BOOT / 下载模式 | ESP32-S3 启动配置脚，谨慎复用 |
| `GPIO1` | SD `MOSI` / `CMD` | microSD |
| `GPIO2` | SD `SCK` / `CLK` | microSD |
| `GPIO3` | SD `MISO` / `D0` | microSD |
| `GPIO4` | LCD `QSPI_SIO0` | AMOLED QSPI 数据 0 |
| `GPIO5` | LCD `QSPI_SI1` | AMOLED QSPI 数据 1 |
| `GPIO6` | LCD `QSPI_SI2` | AMOLED QSPI 数据 2 |
| `GPIO7` | LCD `QSPI_SI3` | AMOLED QSPI 数据 3 |
| `GPIO8` | `I2S_DSDIN` | ES8311 音频数据输入，扬声器播放链路 |
| `GPIO9` | `I2S_SCLK` / BCLK | ES8311、ES7210 共用 I2S 位时钟 |
| `GPIO10` | `I2S_ASDOUT` | ES7210 麦克风 ADC 数据输出到 ESP32-S3 |
| `GPIO11` | `TP_INT` | 触摸中断 |
| `GPIO12` | `LCD_CS` | AMOLED 片选 |
| `GPIO13` | `RTC_INT` | PCF85063ATL RTC 中断 |
| `GPIO14` | `TP_SCL` / `ESP32_SCL` / `RTC_SCL` | 板载共享 I2C SCL |
| `GPIO15` | `TP_SDA` / `ESP32_SDA` / `RTC_SDA` | 板载共享 I2C SDA |
| `GPIO16` | `SYS_OUT` | 电源 / 系统输出相关信号 |
| `GPIO17` | `QMI_INT1` | QMI8658 中断 1 |
| `GPIO18` | `KEY3` | 用户按键，通过 `R18 10K` 上拉到 `VCC3V3` ，按下接地 |
| `GPIO19` | `USB_N` | ESP32-S3 原生 USB D- |
| `GPIO20` | `USB_P` | ESP32-S3 原生 USB D+ |
| `GPIO21` | `QMI_INT2` | QMI8658 中断 2 |
| `GPIO38` | LCD `QSPI_SCL` | AMOLED QSPI 时钟 |
| `GPIO39` | `LCD_RESET` | AMOLED 复位 |
| `GPIO40` | `TP_RESET` | 触摸复位 |
| `GPIO41` | `SDCS` | microSD 片选 |
| `GPIO42` | `I2S_MCLK` | ES8311、ES7210 音频主时钟 |
| `GPIO43` | `U0TXD` | UART0 TX |
| `GPIO44` | `U0RXD` | UART0 RX |
| `GPIO45` | `I2S_LRCK` | ES8311、ES7210 I2S 左右声道时钟 |
| `GPIO46` | `PA_CTRL` | 扬声器功放使能 / 控制 |

LCD 与触摸引脚
| 信号 | GPIO | 说明 |
| --- | --- | --- |
| `LCD_CS` | `GPIO12` | QSPI 片选 |
| `QSPI_SIO0` | `GPIO4` | QSPI 数据 0 |
| `QSPI_SI1` | `GPIO5` | QSPI 数据 1 |
| `QSPI_SI2` | `GPIO6` | QSPI 数据 2 |
| `QSPI_SI3` | `GPIO7` | QSPI 数据 3 |
| `QSPI_SCL` | `GPIO38` | QSPI 时钟 |
| `LCD_RESET` | `GPIO39` | 屏幕复位 |
| `TP_SCL` | `GPIO14` | 触摸 I2C SCL |
| `TP_SDA` | `GPIO15` | 触摸 I2C SDA |
| `TP_INT` | `GPIO11` | 触摸中断 |
| `TP_RESET` | `GPIO40` | 触摸复位 |

音频引脚
| 信号 | GPIO | 连接对象 | 说明 |
| --- | --- | --- | --- |
| `I2S_MCLK` | `GPIO42` | ES8311 / ES7210 | 音频主时钟 |
| `I2S_SCLK` | `GPIO9` | ES8311 / ES7210 | I2S BCLK |
| `I2S_LRCK` | `GPIO45` | ES8311 / ES7210 | I2S WS / LRCK |
| `I2S_DSDIN` | `GPIO8` | ES8311 | ESP32-S3 到 ES8311 的播放数据 |
| `I2S_ASDOUT` | `GPIO10` | ES7210 | ES7210 到 ESP32-S3 的录音数据 |
| `PA_CTRL` | `GPIO46` | 功放 | 扬声器功放控制 |
| `ESP32_SCL` | `GPIO14` | ES8311 / ES7210 控制 | I2C SCL |
| `ESP32_SDA` | `GPIO15` | ES8311 / ES7210 控制 | I2C SDA |

SD 卡引脚
| 信号 | GPIO | 说明 |
| --- | --- | --- |
| `SD_MOSI` / `SD_CMD` | `GPIO1` | SD 命令线 |
| `SD_SCK` / `SD_CLK` | `GPIO2` | SD 时钟 |
| `SD_MISO` / `SD_D0` | `GPIO3` | SD 数据 0 |
| `SDCS` | `GPIO41` | SD 片选 |

按键引脚
| 按键 / 信号 | GPIO / 信号 | 说明 |
| --- | --- | --- |
| `Key3` | `GPIO18` | 用户按键，默认上拉，按下接地 |
| `Key2` | `GPIO0` / `CHIP_PU` | 按键区同时连接 `GPIO0` 与 `CHIP_PU` ，与启动/复位相关，复用需谨慎 |
| `Key1` | `PWRON` | PMU 电源键输入 |
| `SYS_OUT` | `GPIO16` | 系统输出 / 电源相关控制信号 |

I2C 设备汇总
| 设备 | 型号 | I2C 引脚 | 额外中断 / 控制 |
| --- | --- | --- | --- |
| 触摸 | CST9220 | `GPIO14` / `GPIO15` | `TP_INT=GPIO11` 、 `TP_RESET=GPIO40` |
| 电源管理 | AXP2101 | `GPIO14` / `GPIO15` | 通过 PMU 管理电源相关中断 |
| IMU | QMI8658A / QMI8658C | `GPIO14` / `GPIO15` | `QMI_INT1=GPIO17` 、 `QMI_INT2=GPIO21` |
| RTC | PCF85063ATL | `GPIO14` / `GPIO15` | `RTC_INT=GPIO13` |
| 音频 Codec | ES8311 / ES7210 | `GPIO14` / `GPIO15` | I2S 见"音频引脚" |

### 注意事项

- `GPIO14` 和 `GPIO15` 是板载共享 I2C 总线，已经连接多个器件，外接 I2C 设备时需注意地址冲突和总线电平。
- `GPIO19` 和 `GPIO20` 是 ESP32-S3 原生 USB，请勿作为普通 GPIO 随意占用。
- `GPIO43` 和 `GPIO44` 为 UART0，通常用于下载和调试日志。
- `GPIO0` 与启动模式相关，不建议作为普通外设固定占用。
- `GPIO18` 已连接板载 `Key3` ，作为普通 GPIO 使用时需考虑按键上拉和按下接地的电平影响。

## 产品尺寸

![ESP32-S3-Touch-AMOLED-2.16 产品尺寸](https://docs.waveshare.net/assets/images/ESP32-S3-Touch-AMOLED-2.16-details-size-9be8e99d5f546b1b8ce338c394d988fd.webp)

## 开发方式

ESP32-S3-Touch-AMOLED-2.16 支持 Arduino IDE 和 ESP-IDF 两种开发框架，为开发者提供灵活的选择，您可以根据项目需求和个人偏好选择合适的开发工具。

Arduino 通常更容易上手，对初学者和爱好者更加友好。ESP-IDF 提供更完整的开发工具链和更细粒度的系统控制，适合复杂项目或对性能有较高要求的应用。

- **Arduino IDE** 是一款便捷灵活、易于上手的开源电子原型平台。无需太多基础知识，简单学习后即可快速开发。Arduino 拥有庞大的全球用户社区，提供海量开源代码、项目示例和教程，以及丰富的库资源，封装了复杂功能，让开发者能够快速实现各种功能。您可以参考 **[Arduino IDE 开发环境搭建教程](https://docs.waveshare.net/ESP32-S3-Touch-AMOLED-2.16/Arduino)** 完成初始设置，教程中同时提供了相关示例程序供参考。
- **ESP-IDF** 全称 Espressif IoT Development Framework，是乐鑫科技为 ESP 系列芯片推出的专业开发框架。它基于 C 语言开发，包含编译器、调试器、烧录工具等，支持命令行或集成开发环境（如 Visual Studio Code 配合 Espressif IDF 插件）开发，插件提供代码导航、项目管理、调试等功能。我们推荐使用 VS Code 进行开发，具体配置过程可参考 **[ESP-IDF (VS Code) 开发环境搭建教程](https://docs.waveshare.net/ESP32-S3-Touch-AMOLED-2.16/ESP-IDF)** ，教程中同时提供了相关示例程序供参考。

[文档反馈](https://wj.qq.com/s2/26077836/3c81/?q-2-rR1G=https%3A%2F%2Fdocs.waveshare.net%2FESP32-S3-Touch-AMOLED-2.16%2F)