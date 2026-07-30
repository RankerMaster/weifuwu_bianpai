## 一次侧-核反应堆热力学元件
## 四元命名法: e/f[势/流] 1/2[入/出] n[端口号] 0/1[能量域/物质域]

from .baseSystem import BaseComponent, _demo_general
from typing import Dict

class HeatR_Cooling(BaseComponent):
    """
    一次侧-反应堆近端耗散元件
    [IN PORTS]
    IN1 : [能量&物质] 一次侧-反应堆近端
    IN2 : [能量] 反应堆接触面
    [OUT PORTS]
    OUT1 : [能量] 一次侧金属壁（容性环节）
    OUT1 : [能量&物质] 一次侧-反应堆远端
    [PARAMS]
    C_l: 比热容
    C_r: 反应堆比热容
    M_r: 反应堆质量
    R_daona: 导纳系数
    H1: 一次侧-反应堆金属壁等效传热面积
    K: 一次侧-反应堆金属壁等效传热系数
    """
    def __init__(self, params={}, R_eq=None, input_name=None, params_name=None, name="HeatR_Cooling", uid=None):
        def _R_Cooling_eq(e110, e111, e120_diff,
                          f110, f111, f120, 
                          e210, e220, e221, 
                          f210, f220, f221, 
                          *args, 
                          C_l=1, R_daona=1, H1=1, K=1, C_r=1, M_r=1,
                          **kwargs):
            return [
                f111 - f221, 
                e111 - (e221 + (1/R_daona)*f221**2), 
                f110 - (f220 + f210), 
                2*C_l*f111*H1*K*(e110-e210) - f210*(2*C_l*f111+H1*K), 
                C_r * M_r * e120_diff - (f110 + f120),
                f110- (f220 + f221*C_l*(e110-e220))
                ]
        if isinstance(input_name, Dict):
            input_name = [
                input_name.get("e1", ["e110", "e111", "e120"])[:-1],
                [f"{varn}_diff" for varn in input_name.get("e1", ["e110", "e111", "e120"])[-1:]],
                input_name.get("f1", ["f110", "f111", "f120"]),
                input_name.get("e2", ["e210", "e220", "e221"]),
                input_name.get("f2", ["f210", "f220", "f221"]),
                ]
        super().__init__(params=params, func=R_eq or _R_Cooling_eq, input_name=input_name, params_name=params_name, name=name, uid=uid)

class HeatC1_Cooling(BaseComponent):
    """
    一次侧-反应堆容性元件
    [IN PORTS]
    IN1 : [能量] 一次侧-反应堆近端
    [PARAMS]
    Cm: 一次侧-反应堆金属壁等比热容
    Mm: 一次侧-反应堆金属壁等效质量
    """
    def __init__(self, params={}, R_eq=None, input_name=None, params_name=None, name="HeatC1_Cooling", uid=None):
        def _C1_Cooling_eq(f110, e110_diff,
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
        super().__init__(params=params, func=R_eq or _C1_Cooling_eq, input_name=input_name, params_name=params_name, name=name, uid=uid)

class HeatC2_Cooling(BaseComponent):
    """
    一次侧-反应堆远端容性元件
    [IN PORTS]
    IN1 : [能量&物质] 一次侧-反应堆近端
    [INNER VARS]
    rho: [物质] 即时密度
    Q: [能量] 即时能量
    [PARAMS]
    V: 一次侧-反应堆管道等效体积
    rho_map: 饱和密度(温度, 压力)
    h_map: 饱和比焓(温度)
    """
    def __init__(self, params={}, R_eq=None, input_name=None, params_name=None, name="HeatC2_Cooling", uid=None):
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
                Q - h_map(e110)*f111,
                ]
        if isinstance(input_name, Dict):
            _name = input_name.get("e1", ["e110", "e111"])[0][:-4]
            input_name = [
                input_name.get("e1", ["e110", "e111"]),
                input_name.get("f1", ["f110", "f111"]),
                [f"{_name}{varname}" for varname in ["rho", "rho_diff", "Q", "Q_diff"]],
                ]
        super().__init__(params=params, func=R_eq or _C2_eq, input_name=input_name, params_name=params_name, name=name, uid=uid)

class HeatCoolingSource(BaseComponent):
    """
    反应堆流源元件
    [OUT PORTS]
    OUT1 : [能量] 一次侧入口
    [PARAMS]
    source_f211: 流量源
    """
    def __init__(self, params={}, Source_eq=None, input_name=None, params_name=None, name="HeatCoolingSource", uid=None):
        def _HeatCoolingSource_eq(
                f210,
                *args, 
                source_f210=0, 
                **kwargs):#h1为源元件中的焓值
            return [f210 - source_f210]    #物质，能量
        if isinstance(input_name, Dict):
            params_name = ["source_" + input_name.get("f2", ["f210"])[0]]
            input_name =  [
                input_name.get("f2", ["f210"]),
                ]
        super().__init__(params=params, func=Source_eq or _HeatCoolingSource_eq, input_name=input_name, params_name=params_name, name=name, uid=uid)