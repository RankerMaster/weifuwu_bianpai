## 过冷段热力学元件
## 四元命名法: e/f[势/流] 1/2[入/出] n[端口号] 0/1[能量域/物质域]

from .baseSystem import BaseComponent, _demo_general
from typing import Dict

class HeatRC_Supercooling(BaseComponent):
    """
    过冷段近端损耗元件
    [IN PORTS]
    IN1 : [能量&物质] 过冷段入口（下降段出口）
    IN2 : [能量] 金属壁过冷段传热
    [OUT PORTS]
    OUT1: [能量&物质] 过冷段出口
    OUT2: [能量] 金属壁近入口端等效传热
    OUT3: [能量] 金属壁近出口端等效传热
    [GLOBAL VARS]
    L1: [物质] 过冷段水位
    Q_boil: [能量] 沸腾段金属壁传热
    Q_cold: [能量] 过冷段金属壁传热流
    [INNER VARS]
    rho_ss: [物质] 过冷段平均密度
    h_ss: [物质] 过冷段远端比焓 
    [PARAMS]
    Lssg : 过冷段总长
    cchy: 过冷段阻尼
    g: 重力系数
    S: 过冷段管道截面积
    neta: 水位修正系数
    rho_map: 饱和密度(温度, 压力)
    Ts_map: 饱和温度(压力)
    h_map: 饱和比焓(温度)
    """
    def __init__(self, params={}, Alpha_func=None, input_name=None, params_name=None, name="HeatR1_Supercooling", uid=None):
        def _Supercooling_RC_eq(e110, e111, e120, f110, f111, f120,
                                e210, e211, e220, f210, f211, f220, f230,
                                L1, Q_boil, L1_diff,# global_intro
                                rho_ss, h_ss, h_ss_diff, 
                                *args,
                                Lssg=50, cchi=1e-8, g=9.8, S=20, neta=1, 
                                rho_map=_demo_general, Ts_map=_demo_general, h_map=_demo_general,
                                **kwargs):
            return [
                rho_ss*(f111-f211) - (rho_map(e110,e111)*f111-rho_map(e210,e211)*f211), ## 平均密度核算
                rho_ss*cchi*L1_diff**2/2 + abs(rho_ss*g*L1 + e211 - e111), ## R - 压差
                (f211  + rho_ss*S*L1_diff) - f111, ## C - 物质
                h_ss/h_map(e210) - 1,
                (f110 + f120) - (f210 + rho_ss*L1*h_ss_diff), ## C - 能量
                L1*(f120+Q_boil)*f111 - neta*Lssg*f111*(h_map(Ts_map(e210))*f111 - f110), ## L1估算
                e120 + e220 - e110,
                f120 - (f220 + f230), 
                #e220 - e230, ## 热均匀假设
                f210/h_map(e210) - f211, ## 出口饱和
                e210/Ts_map(e211) - 1, ## LookUp Table
            ]

        if isinstance(input_name, Dict):
            _name = input_name.get("e1", ["e110","e111","e120","e130"])[0][:-4]
            input_name = [
                input_name.get("e1", ["e110","e111","e120","e130"]), 
                input_name.get("f1", ["f110","f111","f120","f130"]),
                input_name.get("e2", ["e210","e211","e220","e230"])[:-1], 
                input_name.get("f2", ["f210","f211","f220","f230"]),
                input_name.get("global", ["GLOBAL_L1", "GLOBAL_Q_boil"]),
                [f"{varn}_diff" for varn in input_name.get("global", ["GLOBAL_L1", "GLOBAL_Q_boil"])[:1]],
                [f"{_name}{varname}" for varname in ["rho_ss", "h_ss", "h_ss_diff"]],
                ]
        super().__init__(params=params, func=Alpha_func or _Supercooling_RC_eq, input_name=input_name, params_name=params_name, name=name, uid=uid)

class HeatR_Supercooling(BaseComponent):
    """
    过冷段-金属壁阻性元件
    [IN PORTS]
    IN1 : [能量] 金属壁过冷段传热
    [GLOBAL VARS]
    L1: [物质] 过冷段水位
    [PARAMS]
    Lssg : 过冷段总长
    Hssg : 过冷段金属壁等效传热面积
    K2 : 过冷段金属壁传热系数
    """
    def __init__(self, params={}, Alpha_func=None, input_name=None, params_name=None, name="HeatR_Supercooling", uid=None):
        def _HeatR_Supercooling_eq(
                e110, f110,
                L1,# global
                *args, 
                Lssg=20, Hssg=1, K2=1,
                **kwargs):
            return [
                e110 * L1/Lssg*Hssg*K2 /2 - f110,
            ]
        if isinstance(input_name, Dict):
            input_name = [
                input_name.get("e1", ["e110"]), 
                input_name.get("f1", ["f110"]),
                input_name.get("global", ["GLOBAL_L1"]),
                ]
        super().__init__(params=params, func=Alpha_func or _HeatR_Supercooling_eq, input_name=input_name, params_name=params_name, name=name, uid=uid)
