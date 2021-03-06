﻿using Magician.ViewModels;
using Magician.Views;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Magician.Presenters
{
    internal class TricksPresenter
    {
        internal TricksTab View { get; private set; }

        internal TricksViewModel ViewModel { get; private set; }

        internal TricksPresenter(TricksTab view, List<TrickViewModel> tricks)
        {
            View = view;
            ViewModel = new TricksViewModel(tricks);

            view.DataContext = ViewModel;
        }
    }
}
