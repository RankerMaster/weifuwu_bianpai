import time, warnings, uuid
from typing import Dict

def _demo_general(*args):
    return 1

def get_func_varnames(func):
    input_num = func.__code__.co_argcount
    params_num = func.__code__.co_kwonlyargcount
    all_varnames = func.__code__.co_varnames
    input_name = all_varnames[:input_num]
    params_name = all_varnames[input_num:input_num + params_num]
    return input_name, params_name

def _flatten_list(lis, inner=False):
    res = []
    for itm in lis:
        if not isinstance(itm, str):
            res.extend(_flatten_list(itm, inner=True))
        else:
            res.append(itm)
    return res

def _translate_func(func, input_name, params_name, params={}, to_check=True):
    if not callable(func):
        raise Exception("[DefineError] Function isn\'t callable.")
    
    _input_name, _params_name = get_func_varnames(func)
    out_input_name = _flatten_list(input_name or _input_name)
    out_params_name = _flatten_list(params_name or _params_name)
    
    if isinstance(out_input_name, str):
        out_input_name = out_input_name.replace(" ","").split(",")
    if isinstance(out_params_name, str):
        out_params_name = out_params_name.replace(" ","").split(",")

    if to_check:
        if len(_input_name) != len(out_input_name):
            raise Exception(f"[DefineError] Function has defined {len(_input_name)} inputs for a {out_input_name}-input definition.")
        if len(_params_name) < len(out_params_name):
            raise Exception(f"[DefineError] Function has defined {len(_params_name)} params over a {out_params_name}-params definition.")
        elif len(_params_name) > len(out_params_name):
            warnings.warn(f"[DefineWarn] Function has defined {len(_params_name)} params below a {out_params_name}-params definition.")
        
    def _inner_eq(*args, **kwargs):
        return func(
            *args[:len(out_input_name)],
            **{**params, **kwargs}
            )
    #print({pn: pn_ for pn, pn_ in zip(out_params_name, _params_name)})
    return out_input_name, out_params_name, _inner_eq, {pn: pn_ for pn, pn_ in zip(out_params_name, _params_name)}

class BaseComponent:
    def __init__(self, params={}, func=None, input_name=None, params_name=None, name="BaseComponent", uid=None, to_check=True):
        self._name = name
        self._norm_params = params.copy()
        self._uuid = uid or uuid.uuid3(uuid.NAMESPACE_DNS, f"{name}-{round(time.time()*100000)}")
        self._input_name, self._params_name, self._eq, self._params_map = _translate_func(func, input_name, params_name, params=params, to_check=to_check)
        
    @property
    def equation(self):
        return {
            "uid": self._uuid,
            "name": self._name,
            "equation": self._eq,
            "input_name": self._input_name,
            "params_name": self._params_name,
            "params_map": self._params_map
            }
    
class BaseR(BaseComponent):
    def __init__(self, params={}, R_func=None, input_name=None, params_name=None, name="BaseR", uid=None):
        def _R_eq(e, f, *args, R=1, **kwargs):
            #print("出口势：",e,"出口流：", f)
            return e - R*f
        if isinstance(input_name, Dict):
            input_name = [input_name.get("e1", ["e"]), input_name.get("f1", ["f"])]
        super().__init__(params=params, func=R_func or _R_eq, input_name=input_name, params_name=params_name, name=name, uid=uid)

class BaseC(BaseComponent):
    def __init__(self, params={}, C_func=None, input_name=None, params_name=None, name="BaseC", uid=None):
        def _C_eq(f, e_diff, *args, C=1, **kwargs):
            return f - C*e_diff
        if isinstance(input_name, Dict):
            input_name = [input_name.get("f1", ["f"]), [input_name.get("e1", ["e"])[0]+"_diff"]]
        super().__init__(params=params, func=C_func or _C_eq, input_name=input_name, params_name=params_name, name=name, uid=uid)
        
class BaseI(BaseComponent):
    def __init__(self, params={}, I_func=None, input_name=None, params_name=None, name="BaseI", uid=None):
        def _I_eq(e, e_diff, *args, I=1, **kwargs):
            return e - I*e_diff
        if isinstance(input_name, Dict):
            input_name = [input_name.get("e1", ["e"]), [input_name.get("e1", ["e"])[0]+"_diff"]]
        super().__init__(params=params, func=I_func or _I_eq, input_name=input_name, params_name=params_name, name=name, uid=uid)
        
class BaseGY(BaseComponent):
    def __init__(self, params={}, GY_func=None, input_name=None, params_name=None, name="BaseGY", uid=None):
        def _GY_eq(e1, f1, e2, f2, *args, r_e=1, r_f=None, **kwargs):
            r_f = r_f or 1/r_e
            r_e = r_e or 1/r_f
            return [e2-r_e*f1, f2-r_f*e1]
        if isinstance(input_name, Dict):
            input_name = [input_name.get("e1", ["e1"]), input_name.get("f1", ["f1"]),input_name.get("e2", ["e2"]),input_name.get("f2", ["f2"])]
        super().__init__(params=params, func=GY_func or _GY_eq, input_name=input_name, params_name=params_name, name=name, uid=uid)

class BaseTF(BaseComponent):
    def __init__(self, params={}, TF_func=None, input_name=None, params_name=None, name="BaseTF", uid=None):
        def _TF_eq(e1, f1, e2, f2, *args, r_e=1, r_f=None, **kwargs):
            r_f = r_f or 1/r_e
            r_e = r_e or 1/r_f
            return [e2-r_e*e1, f2-r_f*f1]
        if isinstance(input_name, Dict):
            input_name = [input_name.get("e1", ["e1"]), input_name.get("f1", ["f1"]),input_name.get("e2", ["e2"]),input_name.get("f2", ["f2"])]
        super().__init__(params=params, func=TF_func or _TF_eq, input_name=input_name, params_name=params_name, name=name, uid=uid)

class BaseESource(BaseComponent):
    def __init__(self, params={}, ESource_eq=None, input_name=None, params_name=None, name="BaseESource", uid=None):
        def _ESource_eq(e2, *args, source_e2=0, **kwargs):
            return e2 - source_e2
        if isinstance(input_name, Dict):
            params_name = ["source_" + input_name.get("e2", ["e2"])[0]]
            input_name = [input_name.get("e2", ["e2"])]
        super().__init__(params=params, func=ESource_eq or _ESource_eq, input_name=input_name, params_name=params_name, name=name, uid=uid)

class BaseFSource(BaseComponent):
    def __init__(self, params={}, FSource_eq=None, input_name=None, params_name=None, name="BaseFSource", uid=None):
        def _FSource_eq(f2, *args, source_f2=0, **kwargs):
            return f2 - source_f2
        if isinstance(input_name, Dict):
            params_name = ["source_" + input_name.get("f2", ["f2"])[0]]
            input_name = [input_name.get("f2", ["f2"])]
        super().__init__(params=params, func=FSource_eq or _FSource_eq, input_name=input_name, params_name=params_name, name=name, uid=uid)

class BaseEDetect(BaseComponent):
    def __init__(self, params={}, EDetect_eq=None, input_name=None, params_name=None, name="BaseEDetect", uid=None):
        def _EDetect_eq(f1, *args, **kwargs):
            return [f1]
        if isinstance(input_name, Dict):
            input_name = [input_name.get("f1", ["f1"])]
        super().__init__(params=params, func=EDetect_eq or _EDetect_eq, input_name=input_name, params_name=params_name, name=name, uid=uid)

class BaseFDetect(BaseComponent):
    def __init__(self, params={}, FDetect_eq=None, input_name=None, params_name=None, name="BaseFDetect", uid=None):
        def _FDetect_eq(e1, *args, **kwargs):
            return [e1]
        if isinstance(input_name, Dict):
            input_name = [input_name.get("e1", ["e1"])]
        super().__init__(params=params, func=FDetect_eq or _FDetect_eq, input_name=input_name, params_name=params_name, name=name, uid=uid)

class BaseENode(BaseComponent):
    def __init__(self, params={}, ENode_eq=None, input_name=None, params_name=None, name="BaseENode", uid=None):
        if isinstance(input_name, Dict):
            _e1_names, _e2_names, _f1_names, _f2_names = input_name.get("e1", ["e1"]), input_name.get("e2", ["e2"]), \
                                                         input_name.get("f1", ["f1"]), input_name.get("f2", ["f2"])
            if isinstance(_e1_names, str):
                _e1_names = _e1_names.replace(" ","").split(",")
            if isinstance(_e2_names, str):
                _e2_names = _e2_names.replace(" ","").split(",")
            if isinstance(_f1_names, str):
                _f1_names = _f1_names.replace(" ","").split(",")
            if isinstance(_f2_names, str):
                _f2_names = _f2_names.replace(" ","").split(",")
            e_count = len(_flatten_list(_e1_names)) + len(_flatten_list(_e2_names))
            f1_count = len(_flatten_list(_f1_names))
            f2_count = len(_flatten_list(_f2_names))
            if len(_flatten_list(_e1_names)) != f1_count:
                raise Exception(f"[NodeDefineError] {len(_e1_names)} in-pontiels is imcomptable with {f1_count} in-flows.")
            if len(_flatten_list(_e2_names)) != f2_count:
                raise Exception(f"[NodeDefineError] {len(_e2_names)} out-pontiels is imcomptable with {f2_count} out-flows.")
            input_name = _flatten_list(_e1_names) + _flatten_list(_e2_names) + _flatten_list(_f1_names) + _flatten_list(_f2_names)
            def _ENode_eq(*args):
                return [arg_pre - arg_next for arg_pre, arg_next in zip(args[:e_count-1], args[1:e_count])] + [sum(args[e_count:-f2_count])-sum(args[-f2_count:])]
        elif ENode_eq is None:
            raise Exception("[NodeDefineError] Names of inputs aren\'t defined.")
        else:
            warnings.warn("[NodeDefineWarn] Names of inputs aren\'t strictly defined.")
        super().__init__(params=params, func=ENode_eq or _ENode_eq, input_name=input_name, params_name=params_name, name=name, uid=uid, to_check=False)

class BaseFNode(BaseComponent):
    def __init__(self, params={}, FNode_eq=None, input_name=None, params_name=None, name="BaseFNode", uid=None):
        if isinstance(input_name, Dict):
            _e1_names, _e2_names, _f1_names, _f2_names = input_name.get("e1", ["e1"]), input_name.get("e2", ["e2"]), input_name.get("f1", ["f1"]), input_name.get("f2", ["f2"])
            if isinstance(_e1_names, str):
                _e1_names = _e1_names.replace(" ","").split(",")
            if isinstance(_e2_names, str):
                _e2_names = _e2_names.replace(" ","").split(",")
            if isinstance(_f1_names, str):
                _f1_names = _f1_names.replace(" ","").split(",")
            if isinstance(_f2_names, str):
                _f2_names = _f2_names.replace(" ","").split(",")
            e1_count = len(_flatten_list(_e1_names))
            e2_count = len(_flatten_list(_e2_names))
            f_count = len(_flatten_list(_f1_names)) + len(_flatten_list(_f2_names))
            if e1_count != len(_flatten_list(_f1_names)):
                raise Exception(f"[NodeDefineError] {e1_count} in-pontiels is imcomptable with {len(_f1_names)} in-flows.")
            if e2_count != len(_flatten_list(_f2_names)):
                raise Exception(f"[NodeDefineError] {e2_count} out-pontiels is imcomptable with {len(_f2_names)} out-flows.")
            input_name = _flatten_list(_e1_names) + _flatten_list(_e2_names) + _flatten_list(_f1_names) + _flatten_list(_f2_names)
            def _FNode_eq(*args):
                return [arg_pre - arg_next for arg_pre, arg_next in zip(args[-f_count:-1], args[-f_count+1:])] + [sum(args[:e1_count])-sum(args[e1_count:-f_count])]
        elif FNode_eq is None:
            raise Exception("[NodeDefineError] Names of inputs aren\'t defined.")
        else:
            warnings.warn("[NodeDefineWarn] Names of inputs aren\'t strictly defined.")
        super().__init__(params=params, func=FNode_eq or _FNode_eq, input_name=input_name, params_name=params_name, name=name, uid=uid, to_check=False)
