import scipy.interpolate as ipl
import numpy as np
import pandas as pd
from typing import Iterable
import warnings

class InterPolator1D:
    """
    how:    nearest/zero    -   0阶样条插值
            linear/slinear  -   1阶样条插值
            quadratic       -   2阶样条插值
            cubic           -   3阶样条插值
            previous/next   -   只返回某一点的上/下一个值
    """
    def __init__(self, samples, how="linear"):
        _x, _y = samples
        x_ind = sorted(range(len(_x)), key=lambda ind: _x[ind])
        self._x = [_x[ind] for ind in x_ind]
        self._y = pd.DataFrame([[_y[ind]] for ind in x_ind]).fillna(method='ffill').fillna(method='bfill').values.flatten()
        self._how = self._get_how(how)
        with warnings.catch_warnings():
            warnings.filterwarnings("ignore")
            self._func = ipl.interp1d(x = self._x, y = self._y, kind = self._how)
            self._func_exp = ipl.interp1d(x = self._x, y = self._y, kind = "linear", fill_value="extrapolate")

    def _get_how(self, how):
        return {"0": "nearest", "1": "linear", "2": "quadratic", "3": "cubic"}.get(str(how), how)

    def _config_how(self, how="cubic"):
        self._how = self._get_how(how)
        with warnings.catch_warnings():
            warnings.filterwarnings("ignore")
            self._func = ipl.interp1d(x = self._x, y = self._y, kind = self._how)
            if self._how == "linear":
                self._func_exp = self._func
            else:
                self._func_exp = ipl.interp1d(x = self._x, y = self._y, kind = "linear", fill_value="extrapolate")
        
    def predict(self, x):
        #alpha = 0.2
        with warnings.catch_warnings():
            warnings.filterwarnings("ignore")
            if isinstance(x, Iterable):
                x = np.array(x, dtype="float32")
                return self._func(x)
            try:
                return float(self._func(x))
            except:
                return float(self._func_exp(x))

class InterPolator2D:
    """
    how:    nearest/zero    -   0阶样条插值
            linear/slinear  -   1阶样条插值
            quadratic       -   2阶样条插值
            cubic           -   3阶样条插值
            previous/next   -   只返回某一点的上/下一个值
    """
    def __init__(self, samples, how="cubic"):
        _x, _y, _z = samples
        _xx, _yy = np.meshgrid(_x, _y)
        x_ind = sorted(range(len(_x)), key=lambda ind: _x[ind])
        y_ind = sorted(range(len(_y)), key=lambda ind: _y[ind])
        _xx = _xx[:,x_ind][y_ind,:]
        _yy = _yy[:,x_ind][y_ind,:]
        _z = _z[:,x_ind][y_ind,:]
        x_ind = np.where(~(np.isnan(_z).all(axis=0)))[0]
        y_ind = np.where(~(np.isnan(_z).all(axis=1)))[0]
        self._x = _xx[:,x_ind][y_ind,:]
        self._y = _yy[:,x_ind][y_ind,:]
        _z = _z[:,x_ind][y_ind,:]
        _zz = pd.DataFrame(_z)
        self._z = (_zz.fillna(method='ffill').fillna(method='bfill').values + \
                   _zz.fillna(method='ffill').fillna(method='bfill').T.values.T)/2
        self._how = self._get_how(how)
        with warnings.catch_warnings():
            warnings.filterwarnings("ignore")
            self._func = ipl.interp2d(x = self._x, y = self._y, z = self._z, kind = self._how, bounds_error=False)

    def _get_how(self, how):
        return {"0": "nearest", "1": "linear", "2": "quadratic", "3": "cubic"}.get(str(how), how)

    def _config_how(self, how="cubic"):
        self._how = self._get_how(how)
        with warnings.catch_warnings():
            warnings.filterwarnings("ignore")
            self._func = ipl.interp2d(x = self._x, y = self._y, z = self._z, kind = self._how, bounds_error=False)
        
    def predict(self, *args):
        with warnings.catch_warnings():
            warnings.filterwarnings("ignore")
            if isinstance(args[0], Iterable):
                _x = np.array([args[0] for _ in args[1]], dtype="float32")
            else:
                _x = args[0]
            if isinstance(args[1], Iterable):
                _y = np.array([args[1] for _ in args[0]], dtype="float32")
            else:
                _y = args[1]
            if isinstance(args[0], Iterable) or isinstance(args[-1], Iterable):
                return np.diag(self._func(_x.flatten(), _y.T.flatten())).reshape((len(_x),len(_y)))
            return self._func(_x, _y)[0]
         
class InterPolator:
    """
    how:    nearest/zero    -   0阶样条插值
            linear/slinear  -   1阶样条插值
            quadratic       -   2阶样条插值
            cubic           -   3阶样条插值
            previous/next   -   只返回某一点的上/下一个值
    """
    def __init__(self, samples, how="linear"):
        samples = [np.array(samitm, dtype="float32") for samitm in samples]
        if len(samples) == 2:
            self._intpl = InterPolator1D(samples, how=how)
        elif len(samples) == 3:
            self._intpl = InterPolator2D(samples, how=how)
        else:
            raise Exception(f"[Dimension Error] Couldn't realize a {len(samples)}-dimensional interpolation")

    def __getattr__(self, how):
        return getattr(self._intpl, how)
    
if __name__ == "__main__":
    import matplotlib.pyplot as plt

    ## 1D interpolation
    _x = np.random.permutation(1000)[:50]
    _y = 1/(1+_x**0.3)*(np.abs(np.sin(_x/200))**0.5)
    
    intpl = InterPolator([_x,_y])
    ## intpl.predict
    print(intpl.predict(intpl._x[0]-5))
    
    _y_pred = intpl.predict(np.array(intpl._x, dtype="float32")-1e-6)
    plt.figure()
    plt.plot(intpl._x, _y_pred, "r--")
    plt.scatter(intpl._x, intpl._y, s=2, c="b")

    ## 1D interpolation
    _x = np.random.permutation(1000)[:20]
    _y = np.random.permutation(1000)[:30]
    _z = 1/((1+_x**0.3)[None,:]*(np.abs(np.sin(_y/200))**0.5)[:,None])
    intpl2 = InterPolator([_x,_y,_z])
    print(intpl2.predict(_x[0]-5, _y[0]-5))
    _z_pred = intpl2.predict(sorted(_x-1e-6), sorted(_y-1e-6))
    _z_pred = _z_pred.reshape(intpl2._x.shape)
    fig=plt.figure()
    ax = fig.add_subplot(projection="3d")
    ax.plot_wireframe(intpl2._x, intpl2._y, _z_pred)
    ax.scatter(intpl2._x.flatten(), intpl2._y.flatten(), intpl2._z.flatten(), s=2, c="b")
    
    plt.show()
