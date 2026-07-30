## 下降段热力学元件
## 四元命名法: e/f[势/流] 1/2[入/出] n[端口号] 0/1[能量域/物质域]

from .baseSystem import BaseComponent, _demo_general
from typing import Dict
import math

class HeatAlpha_Des(BaseComponent):
    """
    下降段近端汇流元件（alpha）
    [IN PORTS]
    IN1 : [能量&物质] 给水输入
    IN2 : [能量&物质] 再循环输入
    [OUT PORTS]
    OUT1: [能量&物质] 下降段入口状态
    [PARAMS]
    h_map : 饱和比焓(温度)
    """
    def __init__(self, params={}, Alpha_func=None, input_name=None, params_name=None, name="HeatAlpha_Des", uid=None):
        def _Alpha_eq(e110, e111, e120, e121, f110, f111, f120, f121, e210, e211, f210, f211, 
                      *args,  
                      h_map=_demo_general,
                      **kwargs):
            return [
                f211-(f111+f121),
                f210-(f110+f120),
                ## 拉乌尔定律 Raoult's law 
                e211*(f111+f121)-(f111*e111+f121*e121), ## 压力平均
                e210*(f111+f121)-(f111*e110+f121*e120), ## 温度平均
                ## 入口工质饱和
                f110 - f111*h_map(e110),
                f111 - f121*h_map(e120),
            ]
        if isinstance(input_name, Dict):
            name = input_name.get("e1", ["e110","e111","e120","e121"])[0][:-4]
            input_name = [
                input_name.get("e1", ["e110","e111","e120","e121"]), 
                input_name.get("f1", ["f110","f111","f120","f121"]),
                input_name.get("e2", ["e210","e211"]), 
                input_name.get("f2", ["f210","f211"]),
                ]
        super().__init__(params=params, func=Alpha_func or _Alpha_eq, input_name=input_name, params_name=params_name, name=name, uid=uid)

class HeatRC_Des(BaseComponent):#
    """
    下降段过程耗散元件
    [IN PORTS]
    IN1 : [能量&物质] 下降段入口状态
    [OUT PORTS]
    OUT1: [能量&物质] 下降段出口状态
    [GLOBAL VARS]
    Ld: [物质] 下降段水位
    [INNER VARS]
    hxj2: [能量] 下降段出口比焓
    rho_ss: [能量] 下降段平均密度
    [PARAMS]
    cf : 管壁摩擦系数
    cchy: 下降段阻尼
    g: 重力系数
    S: 下降段管道截面积
    Pg_map : 饱和蒸汽压(温度)
    rho_map : 饱和密度(温度, 压强)
    h_map : 饱和比焓(温度)
    """
    def __init__(self, params={}, Alpha_func=None, input_name=None, params_name=None, name="HeatRC_Des", uid=None):
        def _HeatRC_eq(e110, e111, f110, f111, e210, e211, f210, f211,
                       Ld, Ld_diff,## global
                       hxj2, rho_ss, hxj2_diff,
                       *args, 
                       cf=0.3, cchy=1e-6, g=9.8, S=100,
                       Pg_map=_demo_general, rho_map=_demo_general, h_map=_demo_general,
                       **kwargs):
            return [
                ## 容/阻性元件
                rho_ss*(f111-f211) - (rho_map(e110,e111)*f111-rho_map(e210,e211)*f211), ## 平均密度核算
                f210 - (f110 + rho_map(e210, e211)*S*Ld*hxj2_diff),
                cf*Ld/(S/math.pi)**0.5*Ld_diff**2/4*rho_map(e110,e111) - abs(e111-Pg_map(e210)), ## 范宁公式
                e211 - Pg_map(e210) - (rho_ss*g*Ld - \
                                       rho_ss*cchy*Ld_diff**2/2),
                ## 不可压缩
                f211 - (f111 + rho_ss*S*Ld_diff),
                ## 共温
                e110 - e210,
                ## 出口饱和
                hxj2 - h_map(e210),
                ]

        if isinstance(input_name, Dict):
            _name = input_name.get("e1", ["e110","e111"])[0][:-4]
            input_name = [
                input_name.get("e1", ["e110","e111"]), 
                input_name.get("f1", ["f110","f111"]),
                input_name.get("e2", ["e210","e211"]), 
                input_name.get("f2", ["f210","f211"]),
                input_name.get("global", ["GLOBAL_Ld"]),
                [f"{varn}_diff" for varn in input_name.get("global", ["GLOBAL_Ld"])],
                [f"{_name}{varname}" for varname in ["hxj2", "rho_ss", "hxj2_diff"]],
                ]
        super().__init__(params=params, func=Alpha_func or _HeatRC_eq, input_name=input_name, params_name=params_name, name=name, uid=uid)

class HeatInjetDesSource(BaseComponent):
    """
    下降段给水源元件
    [OUT PORTS]
    OUT1: [能量&物质] 给水状态
    [PARAMS]
    source_e210 : 给水温度
    source_f211 : 给水流量
    """
    def __init__(self, params={}, Source_eq=None, input_name=None, params_name=None, name="HeatInjetDesSource", uid=None):
        def _HeatInjetDesSource_eq(e210, f211, 
                                   *args, 
                                   source_e210=0, source_f211=0, 
                                   **kwargs):#h1为源元件中的焓值
            return [e210 - source_e210, f211 - source_f211]    #1是物质，0是能量
        if isinstance(input_name, Dict):
            params_name = ["source_" + input_name.get("e2", ["e210", "e211"])[0],
                           "source_" + input_name.get("f2", ["f210", "f211"])[1]]
            input_name = [
                input_name.get("e2", ["e210", "e211"])[0],
                input_name.get("f2", ["f210", "f211"])[1],
                ]
        super().__init__(params=params, func=Source_eq or _HeatInjetDesSource_eq, input_name=input_name, params_name=params_name, name=name, uid=uid)
