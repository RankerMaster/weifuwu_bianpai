import os
import matplotlib.pyplot as plt
from matplotlib.font_manager import FontProperties
plt.rc("font", family="Times New Roman", size=10)   #font设置字体大小;family设置字体样式;
plt.rcParams["font.sans-serif"] = "SimHei"      #设置中文字体为黑体
plt.rcParams["axes.unicode_minus"] = False      #将负号正常显示
FONT = FontProperties(fname=os.path.join(r".","simhei.ttf"), size=10)   #设置matplotlib绘图时使用的字体

TYPECOLOR = {
        "能量": "orange",
        "物质": "red",
        "电": "blue",
        "磁": "dodgerblue",
        "力": "purple",
        "力矩": "darkslategrey",
        "位移": "mediumvioletred",
        "角度": "teal",
    }

def _convert_name(text, lenmax=5):
    s = []
    for tind in range(len(text)//lenmax+int(len(text)%lenmax>0)):
        s.append(text[lenmax*tind:lenmax*(tind+1)])
    return "\n".join(s)

def _plot_icon(name, true_name, inputs, outputs):
    if not os.path.exists(os.path.join(r".", "images")):
        os.mkdir(os.path.join(r".", "images"))
    fig = plt.figure(figsize=(4,2))
    ax = fig.add_subplot(1,1,1)
    ax.set_xlim([0.5,5.5])
    ax.set_ylim([0.5,5.5])
    ax.plot(range(2,5), [3]*3, "k--")
    ax.text(3, 4, _convert_name(true_name), fontsize=10,
            verticalalignment="bottom", horizontalalignment="center",
            fontproperties=FONT)
    if len(inputs) > 1:
        ax.plot([2]*5,range(1,6), "k--")
        for inind, ininfo in enumerate(inputs):
            ax.plot(range(1,3), [5-inind*4/(len(inputs)-1)]*2, "k--")
            ax.text(0.8, 5-inind*4/(len(inputs)-1), f"{ininfo['text']}[{ininfo['type']}]",
                    color=TYPECOLOR[ininfo["type"]],
                    verticalalignment="center", horizontalalignment="right",
                    fontproperties=FONT)
    elif len(inputs) == 1:
        ax.plot(range(1,3), [3]*2, "k--")
        ax.text(0.8, 3, f"{inputs[0]['text']}[{inputs[0]['type']}]",
                color=TYPECOLOR[inputs[0]["type"]],
                verticalalignment="center", horizontalalignment="right",
                fontproperties=FONT)
    else:
        ax.plot([2]*3,range(2,5), "k--")
    if len(outputs) > 1:
        ax.plot([4]*5,range(1,6), "k--")
        for outind, outinfo in enumerate(outputs):
            ax.plot(range(4,6), [5-outind*4/(len(outputs)-1)]*2, "k--")
            ax.text(5.2, 5-outind*4/(len(outputs)-1), f"{outinfo['text']}[{outinfo['type']}]",
                    color=TYPECOLOR[outinfo["type"]],
                    verticalalignment="center", horizontalalignment="left",
                    fontproperties=FONT)
    elif len(outputs) == 1:
        ax.plot(range(1,3), [3]*2, "k--")
        ax.text(0.8, 3, f"{outputs[0]['text']}[{outputs[0]['type']}]",
                color=TYPECOLOR[outputs[0]["type"]],
                verticalalignment="center", horizontalalignment="right",
                fontproperties=FONT)
    else:
        ax.plot([4]*3,range(2,5), "k--")
    plt.axis("off")
    fig.tight_layout()
    fig.savefig(os.path.join(r".", "images", f"{name}.png"),
                transparent=True, dpi=200)
    #fig.close()

def plot_all_icon(infos):
    for itm in infos:
        _plot_icon(itm["name"], itm.get("true_name", itm["name"]),
                   itm["inputs"], itm["outputs"])

if __name__ == "__main__":
    """
        {
            "name": "heat-r-cooling",
            "true_name": "一次侧-反应堆近端耗散元件",
            "inputs": [
                {"text": "入口", "type": "能量"},
                {"text": "入口", "type": "物质"},
                {"text": "反应堆", "type": "能量"},
                ],
            "outputs": [
                {"text": "金属壁", "type": "能量"},
                {"text": "近端", "type": "能量"},
                {"text": "近端", "type": "物质"},
                ]
            }, {
            "name": "heat-c1-cooling",
            "true_name": "一一次侧-反应堆容性耗散元件",
            "inputs": [{"text": "接触面", "type": "能量"}],
            "outputs": []
            }, {
            "name": "heat-c2-cooling",
            "true_name": "一次侧-反应堆远端耗散元件",
            "inputs": [
                {"text": "远端", "type": "能量"},
                {"text": "远端", "type": "物质"},
                ],
            "outputs": []
            }, {
            "name": "heat-source-cooling",
            "true_name": "-反应堆流源元件",
            "inputs": [],
            "outputs": [
                {"text": "反应堆", "type": "能量"},
                ]
            },
        
        {
            "name": "heat-r-prime",
            "true_name": "一次侧近端耗散元件",
            "inputs": [
                {"text": "入口", "type": "能量"},
                {"text": "入口", "type": "物质"},
                ],
            "outputs": [
                {"text": "金属壁", "type": "能量"},
                {"text": "近端", "type": "能量"},
                {"text": "近端", "type": "物质"},
                ]
            }, {
            "name": "heat-c1-prime",
            "true_name": "一一次侧-金属壁容性耗散元件",
            "inputs": [{"text": "接触面", "type": "能量"}],
            "outputs": []
            }, {
            "name": "heat-c2-prime",
            "true_name": "一次侧远端耗散元件",
            "inputs": [
                {"text": "远端", "type": "能量"},
                {"text": "远端", "type": "物质"},
                ],
            "outputs": []
            }, {
            "name": "heat-source-prime",
            "true_name": "一次侧流源元件",
            "inputs": [],
            "outputs": [
                {"text": "入口", "type": "能量"},
                {"text": "入口", "type": "物质"},
                ]
            },

           {
            "name": "heat-alpha-des",
            "true_name": "下降段近端汇流元件",
            "inputs": [
                {"text": "给水入口", "type": "能量"},
                {"text": "给水入口", "type": "物质"},
                {"text": "再循环入口", "type": "能量"},
                {"text": "再循环入口", "type": "物质"},
                ],
            "outputs": [
                {"text": "近端", "type": "能量"},
                {"text": "近端", "type": "物质"},
                ]
            }, {
            "name": "heat-rc-des",
            "true_name": "下降段过程耗散元件",
            "inputs": [
                {"text": "近端", "type": "能量"},
                {"text": "近端", "type": "物质"},
                ],
            "outputs": [
                {"text": "远端", "type": "能量"},
                {"text": "远端", "type": "物质"},
                ]
            }, {
            "name": "heat-inject-source-des",
            "true_name": "下降段给水源元件",
            "inputs": [],
            "outputs": [
                {"text": "给水入口", "type": "能量"},
                {"text": "给水入口", "type": "物质"},
                ]
            },

            {
            "name": "heat-rc-boil",
            "true_name": "沸腾段近端耗散元件",
            "inputs": [
                {"text": "入口", "type": "能量"},
                {"text": "入口", "type": "物质"},
                {"text": "金属壁", "type": "能量"},
                ],
            "outputs": [
                {"text": "出口", "type": "能量"},
                {"text": "出口", "type": "物质"},
                {"text": "近端金属壁", "type": "能量"},
                {"text": "远端金属壁", "type": "能量"},
                ]
            }, {
            "name": "heat-r-boil",
            "true_name": "沸腾段-金属壁阻性元件",
            "inputs": [
                {"text": "接触面", "type": "能量"},
                ],
            "outputs": []
            },

            {
            "name": "heat-rc-supercooling",
            "true_name": "过冷段近端耗散元件",
            "inputs": [
                {"text": "入口", "type": "能量"},
                {"text": "入口", "type": "物质"},
                {"text": "金属壁", "type": "能量"},
                ],
            "outputs": [
                {"text": "出口", "type": "能量"},
                {"text": "出口", "type": "物质"},
                {"text": "近端金属壁", "type": "能量"},
                {"text": "远端金属壁", "type": "能量"},
                ]
            }, {
            "name": "heat-r-supercooling",
            "true_name": "过冷段-金属壁阻性元件",
            "inputs": [
                {"text": "接触面", "type": "能量"},
                ],
            "outputs": []
            },
        
            {
            "name": "heat-r-splitter",
            "true_name": "分离器阻性元件",
            "inputs": [
                {"text": "入口", "type": "能量"},
                {"text": "入口", "type": "物质"},
                ],
            "outputs": [
                {"text": "出口", "type": "能量"},
                {"text": "出口", "type": "物质"},
                ]
            },

            {
            "name": "heat-rc-vapour",
            "true_name": "蒸汽腔室耗散元件",
            "inputs": [
                {"text": "入口", "type": "能量"},
                {"text": "入口", "type": "物质"},
                ],
            "outputs": []
            },
            """
        
    infos = [
        {
            "name": "demo",
            "true_name": "多场域配色示意",
            "inputs": [
                {"text": "IN1", "type": "能量"},
                {"text": "IN2", "type": "物质"},
                {"text": "IN3", "type": "力"},
                {"text": "IN4", "type": "力矩"},
                ],
            "outputs": [
                {"text": "OUT1", "type": "电"},
                {"text": "OUT2", "type": "磁"},
                {"text": "OUT3", "type": "位移"},
                {"text": "OUT4", "type": "角度"},
                ]
            },
        {
            "name": "heat-memory",
            "true_name": "饱和延迟元件",
            "inputs": [
                {"text": "循环出口", "type": "能量"},
                {"text": "循环出口", "type": "物质"},
                ],
            "outputs": [
                {"text": "循环入口", "type": "能量"},
                {"text": "循环入口", "type": "物质"},
                ]
            },
        ]
    plot_all_icon(infos)
