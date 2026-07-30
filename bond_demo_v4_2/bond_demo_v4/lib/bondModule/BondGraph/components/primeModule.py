## 一次侧热力学元件
## 四元命名法: e/f[势/流] 1/2[入/出] n[端口号] 0/1[能量域/物质域]

from .baseSystem import BaseComponent, _demo_general
from typing import Dict

class HeatMemeory(BaseComponent):
    """
    饱和延迟元件
    [IN PORTS]
    IN1 : [能量&物质] 循环出口
    [OUT PORTS]
    OUT1 : [能量&物质] 循环入口
    [PARAMS]
    init_T: 初始温度
    init_P: 初始压强
    h_map: 饱和比焓(温度)
    """
    def __init__(self, params={}, Memory_func=None, input_name=None, params_name=None, name="HeatMemory", uid=None):
        def _Memory_eq(e110, e111, f110, f111, e210, e211, f210, f211, *args, init_T=20, init_P=100000, h_map=_demo_general, pre_e110=None, pre_e211=None, **kwargs):
            pre_e110 = pre_e110 or init_T
            pre_e211 = pre_e211 or init_P
            return [1e-3*(e210 - pre_e110), 
                    1e-7*(e111 - pre_e211),
                    f210 - f211*h_map(e210),
                    f110 - f111*h_map(e110),
                    ]
        if isinstance(input_name, Dict):
            if params_name is None:
                params_name = ["init_T", "init_P", "h_map", "pre_" + input_name.get("e1", ["e110", "e111"])[0], "pre_" + input_name.get("e2", ["e210", "e211"])[-1]]
            else:
                params_name += ["pre_" + input_name.get("e1", ["e110", "e111"])[0], 
                                "pre_" + input_name.get("e2", ["e210", "e211"])[-1]]
            input_name = [input_name.get("e1", ["e1"]), input_name.get("f1", ["f1"]),input_name.get("e2", ["e2"]),input_name.get("f2", ["f2"])]
        super().__init__(params=params, func=Memory_func or _Memory_eq, input_name=input_name, params_name=params_name, name=name, uid=uid)

class HeatR_Prime(BaseComponent):
    """
    一次侧近端耗散元件
    [IN PORTS]
    IN1 : [能量&物质] 一次侧入口
    [OUT PORTS]
    OUT1 : [能量] 一次侧金属壁（容性环节）
    OUT1 : [能量&物质] 一次侧出口
    [PARAMS]
    C_l: 比热容
    R_daona: 导纳系数
    H1: 一次侧金属壁等效传热面积
    K: 一次侧金属壁等效传热系数
    """
    def __init__(self, params={}, R_eq=None, input_name=None, params_name=None, name="HeatR_Prime", uid=None):
        def _R_Prime_eq(e110, e111, f110, f111, e210, e220, e221, f210, f220, f221, 
                   *args, 
                   C_l=1, R_daona=1, H1=1, K=1,
                   **kwargs):
            return [
                f111 - f221, 
                e111 - (e221 + (1/R_daona)*f221**2), 
                f110 - (f220 + f210), 
                2*C_l*f111*H1*K*(e110-e210) - f210*(2*C_l*f111+H1*K), 
                f110- (f220 + f221*C_l*(e110-e220))
                ]
        if isinstance(input_name, Dict):
            input_name = [
                input_name.get("e1", ["e110", "e111"]),
                input_name.get("f1", ["f110", "f111"]),
                input_name.get("e2", ["e210", "e220", "e211"]),
                input_name.get("f2", ["f210", "f220", "f211"]),
                ]
        super().__init__(params=params, func=R_eq or _R_Prime_eq, input_name=input_name, params_name=params_name, name=name, uid=uid)

class HeatC1_Prime(BaseComponent):
    """
    一次侧-金属壁容性元件
    [IN PORTS]
    IN1 : [能量] 一次侧入口
    [GLOBAL VARS]
    Q_boil: [能量] 沸腾段金属壁传热
    Q_cold: [能量] 过冷段金属壁传热
    [PARAMS]
    Cm: 一次侧金属壁等比热容
    Mm: 一次侧金属壁等效质量
    """
    def __init__(self, params={}, R_eq=None, input_name=None, params_name=None, name="HeatC1_Prime", uid=None):
        def _C1_Prime_eq(f110, e110_diff,
                   *args, 
                   Cm=1, Mm=1,
                   **kwargs):
            return [
                f110 - Cm*Mm*e110_diff
                ]
        if isinstance(input_name, Dict):
            input_name = [
                input_name.get("f1", ["f110"]),
                [f"{varn}_diff" for varn in input_name.get("e1", ["e110"])],
                ]
        super().__init__(params=params, func=R_eq or _C1_Prime_eq, input_name=input_name, params_name=params_name, name=name, uid=uid)

class HeatC2_Prime(BaseComponent):
    """
    一次侧远端容性元件
    [IN PORTS]
    IN1 : [能量&物质] 一次侧入口
    [INNER VARS]
    rho: [物质] 即时密度
    Q: [能量] 即时能量
    [PARAMS]
    V: 一次侧管道等效体积
    rho_map: 饱和密度(温度, 压力)
    h_map: 饱和比焓(温度)
    """
    def __init__(self, params={}, R_eq=None, input_name=None, params_name=None, name="HeatC2_Prime", uid=None):
        def _C2_eq(e110, e111, f110, f111,
                   rho, rho_diff, Q, Q_diff,
                   *args, 
                   V=100, 
                   rho_map=_demo_general, h_map=_demo_general,
                   **kwargs):
            return [
                f111 - V*rho_diff,
                rho - rho_map(e110, e111),
                f110 - V*Q_diff,
                Q - f111*h_map(e110),
                ]
        if isinstance(input_name, Dict):
            _name = input_name.get("e1", ["e110", "e111"])[0][:-4]
            input_name = [
                input_name.get("e1", ["e110", "e111"]),
                input_name.get("f1", ["f110", "f111"]),
                [f"{_name}{varname}" for varname in ["rho", "rho_diff", "Q", "Q_diff"]],
                ]
        super().__init__(params=params, func=R_eq or _C2_eq, input_name=input_name, params_name=params_name, name=name, uid=uid)

class HeatPrimeSource(BaseComponent):#一次侧源元件
    """
    一次侧流源元件
    [OUT PORTS]
    OUT1 : [能量&物质] 一次侧入口
    [PARAMS]
    source_f211: 流量源
    h_map: 饱和比焓(温度)
    """
    def __init__(self, params={}, Source_eq=None, input_name=None, params_name=None, name="HeatPrimeSource", uid=None):
        def _HeatPrimeSource_eq(
                e210, f210, f211, 
                *args, 
                source_f211=0, 
                h_map=_demo_general, 
                **kwargs):#h1为源元件中的焓值
            return [
                f211 - source_f211, 
                f210 - h_map(e210)*f211
                ]    #物质，能量
        if isinstance(input_name, Dict):
            if params_name is None:
                params_name = ["source_" + input_name.get("f2", ["f2"])[0], "h_map"]
            else:
                params_name[0] = "source_" + input_name.get("f2", ["f2"])[0]
            input_name =  [
                input_name.get("e2", ["e210", "e211"])[0],
                input_name.get("f2", ["f210", "f211"]),
                ]
        super().__init__(params=params, func=Source_eq or _HeatPrimeSource_eq, input_name=input_name, params_name=params_name, name=name, uid=uid)