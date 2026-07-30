## 分离器热力学元件
## 四元命名法: e/f[势/流] 1/2[入/出] n[端口号] 0/1[能量域/物质域]

from .baseSystem import BaseComponent, _demo_general
from typing import Dict

class HeatR_Spiltter(BaseComponent):#
    """
    分离器阻性元件
    [IN PORTS]
    IN1 : [能量&物质] 分离器入口（沸腾段出口）
    [OUT PORTS]
    OUT1: [能量&物质] 分离器出口
    [GLOBAL VARS]
    x: [物质] 气体质量分数
    [PARAMS]
    eta: 分离损耗
    """
    def __init__(self, params={}, Alpha_func=None, input_name=None, params_name=None, name="HeatR1_Spiltter", uid=None):
        def _Spiltter_R1_eq(e110, e111, f110, f111, e210, e211, f210, f211,
                            x, #global_new
                           *args, 
                           eta=0.5,
                           **kwargs):
            return [
                f211-eta*x*f111,
                f210-eta*x*f110,
                e110 - e210,
                e111 - e211,
            ]
        if isinstance(input_name, Dict):
            input_name = [
                input_name.get("e1", ["e110","e111"]), 
                input_name.get("f1", ["f110","f111"]),
                input_name.get("e2", ["e210","e211"]), 
                input_name.get("f2", ["f210","f211"]),
                input_name.get("global", ["GLOBAL_x"]),
                ]
        super().__init__(params=params, func=Alpha_func or _Spiltter_R1_eq, input_name=input_name, params_name=params_name, name=name, uid=uid)