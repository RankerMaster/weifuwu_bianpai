## 蒸汽腔室热力学元件
## 四元命名法: e/f[势/流] 1/2[入/出] n[端口号] 0/1[能量域/物质域]

from .baseSystem import BaseComponent, _demo_general
from typing import Dict

class HeatRC_Vapour(BaseComponent):
    """
    蒸汽腔室逸散元件
    [IN PORTS]
    IN1 : [能量&物质] 蒸汽腔室入口状态（沸腾段出口状态）
    [GLOBAL VARS]
    Ld: [物质] 下降段水位
    L: [物质] 过冷段水位
    [INNER VARS]
    w: [物质] 逸散流量
    d_m: [物质] 等效逸散质量
    [PARAMS]
    P_0 : 出口气压
    V_sd : 等效腔体体积
    C : 逸散损失系数
    Ev: 阀门开度
    S: 蒸汽腔室出口面积
    rho_g_map: 饱和气体密度(温度, 压强)
    h_g_map: 饱和气体比焓(温度, 压强)
    """
    def __init__(self, params={}, Alpha_func=None, input_name=None, params_name=None, name="HeatRC_Vapour", uid=None):
        def _Vapour_RC_eq(e110, e111, f110, f111,
                Ld, L,#global_new
                w, d_m, d_m_diff,
                *args,  #文档上rho_gl没写，但应该是个参数
                P_0=1e5, V_sd=100, C=1, Ev=0.5, S=20,
                rho_g_map=_demo_general, h_g_map=_demo_general,
                **kwargs):
            return [
                w - C*Ev*abs(e110-P_0)**0.5,
                f110 - (w + d_m_diff),
                d_m - rho_g_map(e110, e111)*(V_sd+(Ld-L)*S), ## 腔体空间约束
                f110 - h_g_map(e110)*f111,
            ]
        if isinstance(input_name, Dict):
            _name = input_name.get("e1", ["e1"])[0][:-4]
            input_name = [
                input_name.get("e1", ["e110", "e111"]), 
                input_name.get("f1", ["f110", "f111"]), 
                input_name.get("global", ["GLOBAL_Ld", "GLOBAL_L"]),
                [f"{_name}{varname}" for varname in ["w", "d_m", "d_m_diff"]],
                ]
        super().__init__(params=params, func=Alpha_func or _Vapour_RC_eq, input_name=input_name, params_name=params_name, name=name, uid=uid)